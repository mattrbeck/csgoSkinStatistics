using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
// Microsoft.AspNetCore.HttpOverrides ships its own obsolete IPNetwork; KnownIPNetworks holds the
// System.Net one.
using IPNetwork = System.Net.IPNetwork;

namespace CSGOSkinAPI.Security
{
    // The app is deployed behind a TLS-terminating Caddy reverse proxy and receives plain HTTP from
    // it. Without forwarded-header processing every request looks like it came from the proxy, and
    // the two things that read the connection are silently wrong:
    //
    //   * The "api" rate limiter partitions on Connection.RemoteIpAddress. Behind the proxy that is
    //     one address for the entire internet, so every caller shares a single token bucket - the
    //     per-IP cap on our steamcommunity.com egress stops being per-IP, and one heavy user
    //     429s everyone else.
    //   * Request.IsHttps is false on every request, so anything that follows the scheme sees http.
    //
    // Restoring the client's address and scheme from X-Forwarded-For / X-Forwarded-Proto fixes both
    // - but ONLY if the headers are read from a proxy we trust. Honouring them from any peer is
    // strictly worse than not honouring them at all: a caller could then mint a fresh rate-limit
    // partition per request just by varying a header, escaping the limiter entirely. Hence the
    // known-proxy allowlist below; it is the whole point of this type.
    //
    // Do NOT set ASPNETCORE_FORWARDEDHEADERS_ENABLED instead of this: the built-in switch it turns
    // on clears the known-proxy/known-network lists and trusts every peer, which is exactly the
    // spoofable configuration described above.
    //
    // Scheme only feeds Request.IsHttps today; nothing in this app reads it yet. If HSTS, HTTPS
    // redirection or a Secure-cookie decision ever lands here, the forwarded scheme alone is not
    // enough - a proxy that drops X-Forwarded-Proto would silently downgrade all three. The
    // marketplace branch pairs this middleware with a Production forcing function
    // (MarketTransportSecurity.ForceSecureTransport) for exactly that reason; bring it across at
    // the same time rather than relying on the header being present.
    public static class TransportSecurity
    {
        public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";
        public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

        // Trusted-by-default proxy source ranges: loopback (seeded by the framework) plus the
        // RFC1918 private ranges, which is where the compose stack's Caddy traffic originates -
        // Caddy is a service on the same Docker bridge network as the app, so forwarded headers
        // arrive from a private-range peer, never from the public internet.
        private static readonly string[] DefaultKnownNetworks =
            ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

        public static ForwardedHeadersOptions BuildForwardedHeadersOptions(IConfiguration config)
        {
            var options = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                // Stated, not inherited: this happens to be the framework default, but it is
                // load-bearing. Caddy APPENDS the peer it saw to whatever X-Forwarded-For the
                // client sent, so the rightmost entry is the only trustworthy one and one hop is
                // exactly how far we may unwind. Raising it does NOT simply take the leftmost
                // entry - the trust check re-runs against each address as it is popped, so the
                // chain unwinds only while the popped entry is itself trusted. That is precisely
                // the exposure here: we trust all of RFC1918, so a caller whose own source address
                // is private (another container on the compose network, a LAN host) could send
                // "<anything>, <their own private address>" and, with a raised limit, hand us the
                // partition key of their choice.
                ForwardLimit = 1,
            };
            // Asymmetric on purpose, and worth being precise about:
            //
            //   * KnownNetworks REPLACES the defaults above. Configure it and the RFC1918 ranges
            //     are gone.
            //   * KnownProxies is purely ADDITIVE. It appends to the framework's loopback seed and
            //     leaves KnownNetworks - defaults included - exactly as it was.
            //
            // So setting only KnownProxies to a remote load balancer's address does NOT narrow the
            // trust boundary: all of RFC1918 stays trusted, which on a shared bridge network or in
            // a VPC still means untrusted neighbours can forge a client IP at us. To actually
            // narrow it, set KnownNetworks - a single host is fine, e.g. "198.51.100.7/32".
            // Whatever you end up with is logged at startup by LogTrustedSources, so check
            // the log rather than trusting intent.
            var networks = ReadList(config, KnownNetworksKey) ?? DefaultKnownNetworks;
            foreach (var cidr in networks)
            {
                options.KnownIPNetworks.Add(IPNetwork.Parse(cidr));
            }
            foreach (var ip in ReadList(config, KnownProxiesKey) ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(ip));
            }
            return options;
        }

        // Reads either config shape and trims blanks out. The array form
        // (ForwardedHeaders__KnownNetworks__0=...) is the documented one, but an operator reaching
        // for an env var overwhelmingly writes the scalar (ForwardedHeaders__KnownNetworks=...),
        // and Get<string[]>() answers null for that - which used to fall through to the RFC1918
        // defaults, i.e. fail OPEN relative to what the operator was trying to do. A scalar is
        // accepted as a comma-separated list instead. A malformed entry throws out of Parse and
        // stops the app at boot, which is the right direction to fail in.
        // Returns null only when the key is genuinely absent, so "" and " " narrow the set rather
        // than being mistaken for "unset" - see the blank-entry test.
        private static string[]? ReadList(IConfiguration config, string key)
        {
            var section = config.GetSection(key);
            var values = section.Get<string[]>()
                ?? (section.Value is null ? null : section.Value.Split(','));
            return values?.Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
        }

        // The resolved trust boundary, as a message template. A template rather than a formatted
        // string so the three values arrive as queryable fields, and a const so it can be
        // concatenated into the untrusted-peer warning below (which prints the same resolved set)
        // without either copy drifting from the other.
        public const string TrustedSourcesTemplate =
            "Trusted forwarded-header sources: networks=[{TrustedNetworks}] "
            + "proxies=[{TrustedProxies}] forwardLimit={ForwardLimit}";

        // The arguments TrustedSourcesTemplate expects, in order.
        public static object?[] TrustedSourceValues(ForwardedHeadersOptions options) =>
        [
            string.Join(", ", options.KnownIPNetworks),
            string.Join(", ", options.KnownProxies),
            options.ForwardLimit?.ToString() ?? "unlimited",
        ];

        // Logged once at startup, at Information. A mis-set trust boundary is otherwise entirely
        // silent: the middleware's own diagnostic is Debug and this app's minimum level is
        // Information, so a deployment whose peer address falls outside the trusted set behaves
        // exactly like the unfixed app - one global token bucket - with nothing in the log to say
        // so. This line makes the resolved set greppable in `docker compose logs` on day one, which
        // is why appsettings.json pins CSGOSkinAPI.Security at Information: turning the rest of the
        // app down must not take this with it.
        public static void LogTrustedSources(ILogger logger, ForwardedHeadersOptions options)
            => logger.LogInformation(TrustedSourcesTemplate, TrustedSourceValues(options));

        // Mirrors the framework's own CheckKnownAddress, including the IPv4-mapped unwrap that lets
        // an IPv4 trusted range match the "::ffff:10.0.0.5" a dual-stack Kestrel reports. Used only
        // for the diagnostic below - the middleware makes its own decision and this never gates it.
        // Note loopback is always trusted and cannot be configured away: the framework seeds
        // KnownProxies/KnownIPNetworks with it before we see the options, and we only ever add.
        public static bool IsTrustedProxy(ForwardedHeadersOptions options, IPAddress? peer)
        {
            if (peer is null)
            {
                return false;
            }
            if (peer.IsIPv4MappedToIPv6 && IsTrustedProxy(options, peer.MapToIPv4()))
            {
                return true;
            }
            return options.KnownProxies.Contains(peer)
                || options.KnownIPNetworks.Any(network => network.Contains(peer));
        }

        // The rate limiter's partition key. Not simply RemoteIpAddress.ToString():
        //
        //   * An IPv6 client is routinely handed a whole /64 and can pick a fresh source address
        //     per request, which against a per-address key is an unlimited supply of full token
        //     buckets - the egress protection would exist only for IPv4 users. /64 is the smallest
        //     block anyone is allocated, so it is the narrowest honest unit of "one client".
        //   * "1.2.3.4" and "::ffff:1.2.3.4" are the same client and must not be two buckets.
        //
        // A single key for address-less requests is deliberate and predates this: it caps them all
        // together rather than handing each its own allowance.
        public static string ClientPartitionKey(IPAddress? address)
        {
            if (address is null)
            {
                return "unknown";
            }
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return address.ToString();
            }
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"{new IPAddress(bytes)}/64";
        }
    }

    // Watches for the one deployment mistake that produces no symptom: forwarded headers arriving
    // from a peer outside the trusted set. That is what a host-network deploy, a non-default bridge
    // subnet, or a narrowed-too-far KnownNetworks all look like from in here, and the consequence
    // is the rate limiter silently collapsing back to a single global bucket.
    //
    // One instance per app (Program.cs holds it), so the "once" is per host rather than per
    // process - which keeps it honest under tests, where many hosts share one process.
    public sealed class ForwardedTrustDiagnostics(ForwardedHeadersOptions options, ILogger? logger = null)
    {
        private readonly ILogger logger = logger ?? NullLogger.Instance;
        private int warned;

        // Call BEFORE the forwarded-headers middleware: it needs the peer's real address, which
        // that middleware is about to overwrite. Returns true when it emitted the warning.
        public bool Inspect(HttpContext context)
        {
            // The common path once warned (and on every request of a healthy deployment) is a
            // single volatile read.
            if (Volatile.Read(ref warned) != 0)
            {
                return false;
            }
            var headers = context.Request.Headers;
            if (!headers.ContainsKey("X-Forwarded-For") && !headers.ContainsKey("X-Forwarded-Proto"))
            {
                return false;
            }
            var peer = context.Connection.RemoteIpAddress;
            if (TransportSecurity.IsTrustedProxy(options, peer))
            {
                return false;
            }
            if (Interlocked.Exchange(ref warned, 1) != 0)
            {
                return false;
            }
            // The peer, and the resolved trust boundary it failed against, are both fields - the
            // log line alone is enough to act on without also being a sentence to parse.
            logger.LogWarning(
                "Ignoring forwarded headers from untrusted peer {UntrustedPeer} - if that is your "
                + "reverse proxy, the rate limiter is keying on it instead of on real client IPs. "
                + "Add it to ForwardedHeaders:KnownNetworks. "
                + TransportSecurity.TrustedSourcesTemplate,
                [peer?.ToString() ?? "(no address)", .. TransportSecurity.TrustedSourceValues(options)]);
            return true;
        }
    }
}
