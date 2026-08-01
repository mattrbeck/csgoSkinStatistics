using System.Net;
using CSGOSkinAPI.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace csgoSkinStatistics.Tests.Security;

// The app sits behind a TLS-terminating Caddy on the same compose network, so every request reaches
// it from the proxy's private address over plain HTTP. These tests pin the two halves of the fix:
//
//   1. X-Forwarded-For / X-Forwarded-Proto from a TRUSTED peer are honoured, so the rate limiter's
//      per-IP partition key is the real client and Request.IsHttps is the real scheme. Without this
//      the whole internet shares one token bucket and one heavy caller 429s everybody.
//   2. The same headers from an UNTRUSTED peer are IGNORED. This is the half that makes the fix
//      worth more than a one-liner: honouring forwarded IPs from anyone lets a caller mint a fresh
//      partition per request and walk straight past the limiter.
//
// The payoff test at the bottom asserts the partitioning end to end rather than just that a header
// was parsed - that behaviour is the entire reason this middleware exists here.

// TestServer connections have no RemoteIpAddress, and the trust check (KnownProxies /
// KnownIPNetworks) has nothing to judge without one. An IStartupFilter is the only place a
// middleware can run BEFORE Program.cs's pipeline, i.e. before UseForwardedHeaders: it stamps the
// peer address on the way in, and - via OnStarting, which fires once the response begins and so
// after the forwarded headers have been applied - echoes what the rest of the pipeline ended up
// seeing back as response headers. Reading it off the response keeps every assertion per-request
// with no shared mutable state.
file sealed class PeerProbeStartupFilter(IPAddress peer) : IStartupFilter
{
    public const string RemoteIpHeader = "X-Test-Observed-Remote-Ip";
    public const string HttpsHeader = "X-Test-Observed-Is-Https";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = peer;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[RemoteIpHeader] =
                    context.Connection.RemoteIpAddress?.ToString() ?? "(null)";
                context.Response.Headers[HttpsHeader] = context.Request.IsHttps ? "true" : "false";
                return Task.CompletedTask;
            });
            await nextMiddleware();
        });
        next(app);
    };
}

// One host per trust configuration, shared by the whole class. The factories are cheap to construct
// and only boot a host on first CreateClient, so a test pays for the ones it actually uses.
public sealed class ForwardedHeadersFixture : IDisposable
{
    // A compose-network address: inside the default RFC1918 trusted set, i.e. what Caddy looks like.
    public static readonly IPAddress TrustedPeer = IPAddress.Parse("10.0.0.5");
    // TEST-NET-3, outside loopback and RFC1918: a peer we must never take forwarded headers from.
    public static readonly IPAddress UntrustedPeer = IPAddress.Parse("203.0.113.9");

    private readonly List<ApiFactory> _factories = [];

    // Default configuration, trusted peer.
    public ApiFactory Trusted { get; }

    // Default configuration, peer outside the trusted set.
    public ApiFactory Untrusted { get; }

    // Operator override: both keys set. 203.0.113.9 is trusted here only because
    // ForwardedHeaders:KnownProxies names it.
    public ApiFactory CustomTrust { get; }

    // The same override, hit from a peer that WOULD have been trusted by default. Proves the
    // configured KnownNetworks replaced the RFC1918 defaults instead of adding to them.
    public ApiFactory CustomTrustFromRfc1918Peer { get; }

    // Only KnownProxies is set - the realistic "we moved to one remote load balancer" action. That
    // key is additive, so this pins that RFC1918 is STILL trusted afterwards.
    public ApiFactory ProxiesOnlyTrustFromRfc1918Peer { get; }

    // The shape a dual-stack Kestrel actually reports for a Docker bridge peer: the RFC1918 address
    // mapped into IPv6. The trusted set is written in IPv4, so this pins that the deployment's real
    // connections are matched and the defaults aren't quietly inert in production.
    public ApiFactory MappedTrustedPeer { get; }

    // Trusted peer, but with the token bucket wound right down so partitioning is observable. The
    // tightened limit lives in this factory's own Settings and therefore in this host only - it
    // cannot leak into the rest of the suite.
    public ApiFactory Partitioned { get; }

    // A second wound-down host, so the IPv6 partitioning test starts from full buckets rather than
    // whatever the IPv4 one left behind.
    public ApiFactory PartitionedIpv6 { get; }

    public ForwardedHeadersFixture()
    {
        Trusted = Create(TrustedPeer);
        Untrusted = Create(UntrustedPeer);
        MappedTrustedPeer = Create(IPAddress.Parse("::ffff:172.18.0.2"));
        CustomTrust = Create(UntrustedPeer, CustomTrustSettings);
        CustomTrustFromRfc1918Peer = Create(TrustedPeer, CustomTrustSettings);
        ProxiesOnlyTrustFromRfc1918Peer = Create(TrustedPeer, new Dictionary<string, string?>
        {
            [$"{TransportSecurity.KnownProxiesKey}:0"] = "203.0.113.9",
        });
        Partitioned = Create(TrustedPeer, TightBucket);
        PartitionedIpv6 = Create(TrustedPeer, TightBucket);
    }

    private static Dictionary<string, string?> CustomTrustSettings => new()
    {
        [$"{TransportSecurity.KnownNetworksKey}:0"] = "198.51.100.0/24",
        [$"{TransportSecurity.KnownProxiesKey}:0"] = "203.0.113.9",
    };

    private static Dictionary<string, string?> TightBucket => new()
    {
        ["RateLimiting:TokenLimit"] = "2",
        ["RateLimiting:TokensPerPeriod"] = "1",
        // Long enough that nothing replenishes while the test runs, so the counts are exact.
        ["RateLimiting:ReplenishmentPeriodSeconds"] = "600",
        // No queue: an over-budget request is rejected immediately instead of waiting.
        ["RateLimiting:QueueLimit"] = "0",
    };

    private ApiFactory Create(IPAddress peer, Dictionary<string, string?>? settings = null)
    {
        var factory = new ApiFactory
        {
            ConfigureExtraServices = services =>
                services.AddSingleton<IStartupFilter>(new PeerProbeStartupFilter(peer)),
        };
        foreach (var (key, value) in settings ?? [])
        {
            factory.Settings[key] = value;
        }
        _factories.Add(factory);
        return factory;
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }
}

public sealed class ForwardedHeadersTests(ForwardedHeadersFixture fixture)
    : IClassFixture<ForwardedHeadersFixture>
{
    // Every await in this file is bounded: a hung request has to fail the test, not stall the run.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string ClientIp = "198.51.100.42";

    // GET /api with no parameters is answered as a 400 without any outbound call, which makes it the
    // cheapest way to put a request through the whole pipeline - rate limiter included, since the
    // limiter runs before the endpoint and answers 429 in its place.
    private static async Task<HttpResponseMessage> Probe(
        ApiFactory factory, string? forwardedFor = null, string? forwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        if (forwardedFor != null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        if (forwardedProto != null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }
        using var client = factory.CreateClient();
        return await client.SendAsync(request).WaitAsync(RequestTimeout);
    }

    private static string ObservedRemoteIp(HttpResponseMessage response)
        => response.Headers.GetValues(PeerProbeStartupFilter.RemoteIpHeader).Single();

    private static bool ObservedIsHttps(HttpResponseMessage response)
        => response.Headers.GetValues(PeerProbeStartupFilter.HttpsHeader).Single() == "true";

    // --- the options builder itself -------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void Defaults_trust_loopback_and_the_private_ranges_only()
    {
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config());

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
        // One hop, stated rather than inherited from the framework. It is what stops a caller whose
        // own source address is inside a trusted range from unwinding the chain past the entry the
        // proxy appended; see the chain test below.
        Assert.Equal(1, options.ForwardLimit);
        // The framework seeds loopback; we add the three RFC1918 ranges the compose network uses.
        Assert.Equal(4, options.KnownIPNetworks.Count);
        foreach (var range in new[] { "10.0.0.0", "172.16.0.0", "192.168.0.0" })
        {
            Assert.Contains(options.KnownIPNetworks, n => n.BaseAddress.Equals(IPAddress.Parse(range)));
        }
        Assert.Contains(options.KnownProxies, p => p.Equals(IPAddress.IPv6Loopback));
        // No public range is trusted out of the box - that would be the spoofable configuration.
        Assert.DoesNotContain(options.KnownIPNetworks,
            n => n.Contains(IPAddress.Parse("203.0.113.9")));
    }

    [Fact]
    public void A_configured_KnownNetworks_replaces_the_default_ranges()
    {
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config(
            ($"{TransportSecurity.KnownNetworksKey}:0", "198.51.100.0/24"),
            ($"{TransportSecurity.KnownProxiesKey}:0", "198.51.100.7")));

        // Loopback plus the one configured CIDR: the RFC1918 defaults are gone, so an operator who
        // moves the proxy off the compose network stops trusting ranges they no longer control.
        Assert.Equal(2, options.KnownIPNetworks.Count);
        Assert.Contains(options.KnownIPNetworks,
            n => n.BaseAddress.Equals(IPAddress.Parse("198.51.100.0")));
        Assert.DoesNotContain(options.KnownIPNetworks,
            n => n.BaseAddress.Equals(IPAddress.Parse("10.0.0.0")));
        Assert.Contains(options.KnownProxies, p => p.Equals(IPAddress.Parse("198.51.100.7")));
    }

    [Fact]
    public void KnownProxies_is_additive_and_leaves_the_default_ranges_trusted()
    {
        // The asymmetry that matters, pinned rather than implied. Setting only KnownProxies - the
        // realistic "we moved to one remote load balancer" action - appends to the loopback seed
        // and does NOT touch KnownNetworks, so every RFC1918 address stays trusted. An operator who
        // reads that as "I have narrowed the trust boundary" is wrong, and on a shared bridge
        // network or in a VPC the neighbours they did not mean to trust can still forge a client
        // IP. Narrowing needs KnownNetworks; the startup line prints what was actually resolved.
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config(
            ($"{TransportSecurity.KnownProxiesKey}:0", "203.0.113.9")));

        Assert.Equal(4, options.KnownIPNetworks.Count);
        Assert.True(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("10.0.0.5")));
        Assert.True(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("192.168.1.1")));
        Assert.True(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("203.0.113.9")));
    }

    [Fact]
    public void Blank_entries_are_dropped_rather_than_taking_the_app_down_at_boot()
    {
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config(
            ($"{TransportSecurity.KnownNetworksKey}:0", "  "),
            ($"{TransportSecurity.KnownNetworksKey}:1", " 198.51.100.0/24 "),
            ($"{TransportSecurity.KnownProxiesKey}:0", "")));

        Assert.Equal(2, options.KnownIPNetworks.Count);
        Assert.Contains(options.KnownIPNetworks,
            n => n.BaseAddress.Equals(IPAddress.Parse("198.51.100.0")));
    }

    [Fact]
    public void An_all_blank_KnownNetworks_narrows_to_loopback_instead_of_reverting_to_the_defaults()
    {
        // Deliberately fail CLOSED: a botched value trusts less, never more. The cost is that the
        // limiter collapses to one bucket behind a proxy that is now untrusted, which is exactly
        // what the startup line and the untrusted-peer warning exist to make visible.
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config(
            ($"{TransportSecurity.KnownNetworksKey}:0", "   ")));

        Assert.Single(options.KnownIPNetworks);
        Assert.False(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("10.0.0.5")));
    }

    [Fact]
    public void A_scalar_config_value_is_honoured_rather_than_falling_back_to_the_defaults()
    {
        // ForwardedHeaders__KnownNetworks=198.51.100.0/24 is what an operator actually types; the
        // indexed array form is the one the binder wants. Reading only the array form meant a
        // scalar bound to null and silently reverted to trusting all of RFC1918 - failing OPEN
        // against the operator's intent, which was to narrow the boundary.
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config(
            (TransportSecurity.KnownNetworksKey, "198.51.100.0/24, 203.0.113.0/24"),
            (TransportSecurity.KnownProxiesKey, "192.0.2.7")));

        Assert.Equal(3, options.KnownIPNetworks.Count); // loopback + the two configured
        Assert.False(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("10.0.0.5")));
        Assert.True(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("198.51.100.7")));
        Assert.True(TransportSecurity.IsTrustedProxy(options, IPAddress.Parse("192.0.2.7")));
    }

    [Fact]
    public void A_malformed_value_stops_the_app_at_boot_rather_than_being_skipped()
    {
        // Loud beats lenient: skipping an unparseable CIDR would silently leave the boundary at
        // whatever the rest of the list happened to say.
        Assert.ThrowsAny<Exception>(() => TransportSecurity.BuildForwardedHeadersOptions(
            Config(($"{TransportSecurity.KnownNetworksKey}:0", "not-a-cidr"))));
    }

    [Fact]
    public void The_startup_line_names_the_resolved_networks_proxies_and_hop_limit()
    {
        // The one thing an operator can grep for in `docker compose logs` to see what the app
        // actually trusts, rather than what they meant it to.
        var description = TransportSecurity.DescribeTrustedSources(
            TransportSecurity.BuildForwardedHeadersOptions(Config(
                ($"{TransportSecurity.KnownProxiesKey}:0", "203.0.113.9"))));

        Assert.Contains("10.0.0.0/8", description);
        Assert.Contains("192.168.0.0/16", description);
        Assert.Contains("203.0.113.9", description);
        Assert.Contains("forwardLimit=1", description);
    }

    // --- forwarded headers end to end -----------------------------------------------------

    [Fact]
    public async Task Forwarded_for_from_a_trusted_proxy_is_honoured()
    {
        using var response = await Probe(fixture.Trusted, forwardedFor: ClientIp);

        // The connection the rest of the pipeline sees - including the rate limiter's partition
        // key - is the real client, not Caddy.
        Assert.Equal(ClientIp, ObservedRemoteIp(response));
    }

    [Fact]
    public async Task Without_a_forwarded_header_the_peer_address_stands()
    {
        using var response = await Probe(fixture.Trusted);

        Assert.Equal(ForwardedHeadersFixture.TrustedPeer.ToString(), ObservedRemoteIp(response));
        Assert.False(ObservedIsHttps(response));
    }

    [Fact]
    public async Task Forwarded_for_from_an_untrusted_peer_is_ignored()
    {
        // The anti-spoofing guarantee. If this ever passes the forwarded value through, any caller
        // can hand themselves a brand-new rate-limit partition on every request just by changing a
        // header, and the limiter protecting our steamcommunity.com egress stops existing.
        using var response = await Probe(fixture.Untrusted, forwardedFor: ClientIp);

        Assert.Equal(ForwardedHeadersFixture.UntrustedPeer.ToString(), ObservedRemoteIp(response));
        Assert.NotEqual(ClientIp, ObservedRemoteIp(response));
    }

    [Fact]
    public async Task A_client_supplied_forwarded_for_cannot_outrank_the_one_the_proxy_appends()
    {
        // Caddy APPENDS the peer it saw to any X-Forwarded-For the client sent, so a caller who
        // forges the header produces "forged, their real address". ForwardLimit=1 takes the
        // rightmost - the entry Caddy wrote - and stops.
        //
        // The attacker here is a neighbour on the compose network, so the address Caddy appends is
        // 10.0.0.99: private, and therefore itself inside the trusted set. That detail is the whole
        // point of the test. Raising ForwardLimit does not simply take the leftmost entry - the
        // trust check re-runs against each address as it is popped, so the chain unwinds only while
        // the popped entry is trusted too. With a public address in that slot the chain stops
        // either way and the test would pass at any limit, proving nothing. With a private one,
        // ForwardLimit=2 (or null) pops 10.0.0.99, finds it trusted, and hands the caller
        // 203.0.113.77 - a partition key of their choosing.
        using var response = await Probe(fixture.Trusted, forwardedFor: "203.0.113.77, 10.0.0.99");

        Assert.Equal("10.0.0.99", ObservedRemoteIp(response));
    }

    [Fact]
    public async Task An_ipv4_mapped_private_peer_is_trusted()
    {
        // What the compose deployment actually looks like on a dual-stack listener.
        using var response = await Probe(fixture.MappedTrustedPeer, forwardedFor: ClientIp);

        Assert.Equal(ClientIp, ObservedRemoteIp(response));
    }

    [Fact]
    public async Task Forwarded_proto_from_an_untrusted_peer_is_ignored()
    {
        using var response = await Probe(fixture.Untrusted, forwardedProto: "https");

        Assert.False(ObservedIsHttps(response));
    }

    [Fact]
    public async Task Forwarded_proto_from_a_trusted_proxy_makes_the_request_https()
    {
        using var response = await Probe(
            fixture.Trusted, forwardedFor: ClientIp, forwardedProto: "https");

        Assert.True(ObservedIsHttps(response));
        Assert.Equal(ClientIp, ObservedRemoteIp(response));
    }

    // --- configuration override ----------------------------------------------------------

    [Fact]
    public async Task A_configured_known_proxy_is_trusted_even_though_its_address_is_public()
    {
        // ForwardedHeaders:KnownProxies names 203.0.113.9, which the defaults reject: the override
        // is genuinely reaching the middleware, not just being parsed.
        using var response = await Probe(fixture.CustomTrust, forwardedFor: ClientIp);

        Assert.Equal(ClientIp, ObservedRemoteIp(response));
    }

    [Fact]
    public async Task Configuring_KnownNetworks_stops_the_default_private_ranges_being_trusted()
    {
        // Same host configuration, but the peer is the RFC1918 address the defaults would have
        // trusted. Setting KnownNetworks replaced them, so it no longer is.
        using var response = await Probe(fixture.CustomTrustFromRfc1918Peer, forwardedFor: ClientIp);

        Assert.Equal(ForwardedHeadersFixture.TrustedPeer.ToString(), ObservedRemoteIp(response));
    }

    [Fact]
    public async Task Configuring_only_KnownProxies_leaves_the_default_private_ranges_trusted()
    {
        // The counterpart, end to end, because the difference is easy to assume away: this host
        // names a remote proxy in KnownProxies and sets nothing else, and the RFC1918 peer is STILL
        // trusted - its forwarded header is honoured. An operator who set only KnownProxies has not
        // narrowed anything. The earlier test could not catch this because it sets both keys at
        // once; this one changes exactly one variable.
        using var response = await Probe(
            fixture.ProxiesOnlyTrustFromRfc1918Peer, forwardedFor: ClientIp);

        Assert.Equal(ClientIp, ObservedRemoteIp(response));
    }

    // --- the untrusted-peer warning -------------------------------------------------------

    private static (ForwardedTrustDiagnostics Diagnostics, List<string> Log) Diagnostics(
        params (string Key, string Value)[] settings)
    {
        List<string> log = [];
        var options = TransportSecurity.BuildForwardedHeadersOptions(Config(settings));
        return (new ForwardedTrustDiagnostics(options, log.Add), log);
    }

    private static DefaultHttpContext Request(string peer, bool forwarded = true)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (forwarded)
        {
            context.Request.Headers["X-Forwarded-For"] = ClientIp;
        }
        return context;
    }

    [Fact]
    public void A_forwarded_header_from_an_untrusted_peer_is_reported_once()
    {
        // Ignoring the header is correct but invisible, and the shapes that produce it - a
        // host-network deploy, a non-default bridge subnet, a KnownNetworks narrowed past the real
        // proxy - all look like a healthy app whose limiter has quietly collapsed to one bucket.
        // The middleware's own diagnostic for this is Debug and the app's minimum is Information,
        // so nothing reaches the log without this.
        var (diagnostics, log) = Diagnostics();

        Assert.True(diagnostics.Inspect(Request("203.0.113.9")));

        var line = Assert.Single(log);
        Assert.Contains("203.0.113.9", line);
        // Names the fix, and prints what is actually trusted so the log alone is enough to act on.
        Assert.Contains(TransportSecurity.KnownNetworksKey, line);
        Assert.Contains("10.0.0.0/8", line);

        // Once, not once per request: this must not become a log flood under load.
        Assert.False(diagnostics.Inspect(Request("203.0.113.9")));
        Assert.False(diagnostics.Inspect(Request("198.51.100.1")));
        Assert.Single(log);
    }

    [Fact]
    public void A_healthy_deployment_says_nothing()
    {
        var (diagnostics, log) = Diagnostics();

        // Trusted peer with a forwarded header - the normal case, silent.
        Assert.False(diagnostics.Inspect(Request("10.0.0.5")));
        // Untrusted peer with no forwarded header - i.e. an ordinary direct client, not a proxy
        // problem. Warning here would fire on every request of a directly-exposed deployment.
        Assert.False(diagnostics.Inspect(Request("203.0.113.9", forwarded: false)));
        Assert.Empty(log);
    }

    [Fact]
    public void The_warning_fires_for_an_ipv4_mapped_peer_outside_the_trusted_set()
    {
        var (diagnostics, log) = Diagnostics();

        Assert.False(diagnostics.Inspect(Request("::ffff:10.0.0.5")));
        Assert.True(diagnostics.Inspect(Request("::ffff:203.0.113.9")));
        Assert.Single(log);
    }

    [Fact]
    public void The_warning_fires_when_KnownNetworks_is_narrowed_past_the_real_proxy()
    {
        // The blank-value and typo cases both land here.
        var (diagnostics, log) = Diagnostics(($"{TransportSecurity.KnownNetworksKey}:0", "  "));

        Assert.True(diagnostics.Inspect(Request("10.0.0.5")));
        Assert.Single(log);
    }

    // --- the partition key ----------------------------------------------------------------

    [Theory]
    // IPv4 is used as-is.
    [InlineData("198.51.100.42", "198.51.100.42")]
    // An IPv4-mapped address is the same client as its IPv4 form and must not be a second bucket.
    [InlineData("::ffff:198.51.100.42", "198.51.100.42")]
    // IPv6 collapses to its /64. A client handed a routed /64 - which is the standard residential
    // and cloud allocation - can otherwise pick a fresh source address per request and draw an
    // unlimited number of full token buckets, leaving the egress protection IPv4-only.
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void Partition_keys_identify_a_client_rather_than_an_address(string address, string expected)
        => Assert.Equal(expected, TransportSecurity.ClientPartitionKey(IPAddress.Parse(address)));

    [Fact]
    public void Different_ipv6_prefixes_stay_in_different_partitions()
    {
        // /64 is the narrowest block anyone is actually allocated, so it is as far as the key can
        // be widened without starting to lump unrelated clients together.
        Assert.NotEqual(
            TransportSecurity.ClientPartitionKey(IPAddress.Parse("2001:db8:1:2::1")),
            TransportSecurity.ClientPartitionKey(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void An_address_less_request_keeps_the_shared_unknown_partition()
    {
        // Unchanged behaviour, restated: they are capped together rather than each given their own
        // allowance. (It is also what every other test in this assembly runs on, since TestServer
        // leaves RemoteIpAddress null.)
        Assert.Equal("unknown", TransportSecurity.ClientPartitionKey(null));
    }

    // --- the payoff: the rate limiter partitions per real client --------------------------

    [Fact]
    public async Task Forwarded_client_ips_land_in_separate_rate_limit_partitions()
    {
        // This is the behaviour the whole change exists to produce. The host behind this factory
        // has a two-token bucket that does not replenish during the test, so the partition each
        // request lands in is directly observable in the status codes.
        const string first = "198.51.100.10";
        const string second = "198.51.100.11";

        // Two requests drain the first client's bucket...
        for (var i = 0; i < 2; i++)
        {
            using var allowed = await Probe(fixture.Partitioned, forwardedFor: first);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode); // reached the endpoint
            Assert.Equal(first, ObservedRemoteIp(allowed));
        }

        // ...so its third is rejected.
        using (var rejected = await Probe(fixture.Partitioned, forwardedFor: first))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        // A different forwarded client is untouched by that: its own bucket is full. Before the
        // forwarded-headers fix both of these were the proxy's single address and this request
        // would have been rejected too - one heavy user locking out everyone else.
        using (var otherClient = await Probe(fixture.Partitioned, forwardedFor: second))
        {
            Assert.Equal(HttpStatusCode.BadRequest, otherClient.StatusCode);
        }

        // And the same forwarded client keeps drawing on the same drained bucket, so a caller can't
        // escape the limiter by reconnecting.
        using (var stillRejected = await Probe(fixture.Partitioned, forwardedFor: first))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, stillRejected.StatusCode);
        }

        // The second client's bucket drains on its own schedule, confirming it really was separate
        // rather than merely lagging one request behind.
        using (var secondAllowed = await Probe(fixture.Partitioned, forwardedFor: second))
        {
            Assert.Equal(HttpStatusCode.BadRequest, secondAllowed.StatusCode);
        }
        using (var secondRejected = await Probe(fixture.Partitioned, forwardedFor: second))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, secondRejected.StatusCode);
        }
    }

    [Fact]
    public async Task An_ipv6_client_cannot_rotate_addresses_to_refill_its_bucket()
    {
        // The same payoff from the other side. Keying on the raw address would make each of these
        // a fresh full bucket, so an IPv6 client - who typically holds a routed /64 - could walk
        // straight past the limiter while IPv4 clients stayed capped. They share a /64 and so must
        // share a bucket.
        const string prefix = "2001:db8:1:2";

        using (var first = await Probe(fixture.PartitionedIpv6, forwardedFor: $"{prefix}::1"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        }
        using (var second = await Probe(fixture.PartitionedIpv6, forwardedFor: $"{prefix}::2"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        }
        // Third address, same /64, and the bucket those two drained is already empty.
        using (var third = await Probe(fixture.PartitionedIpv6, forwardedFor: $"{prefix}:aaaa::3"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        }

        // A genuinely different /64 is a different client and still has its own allowance.
        using (var elsewhere = await Probe(fixture.PartitionedIpv6, forwardedFor: "2001:db8:1:3::1"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        }
    }
}
