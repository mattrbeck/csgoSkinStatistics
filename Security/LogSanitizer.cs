namespace CSGOSkinAPI.Security
{
    // Renders untrusted values for the log. Lives here rather than on either of its callers
    // because it belongs to neither: the inspect-link decoder logs the caller's ?url= through it,
    // and the controller logs the caller's vanity name through it. Putting it on one would make
    // the other reach across a layer for it.
    internal static class LogSanitizer
    {
        // Longest prefix of an untrusted value we will put in a log line.
        internal const int MaxLoggedLength = 200;

        // Renders an untrusted request value for the log. Applied to the two values a caller
        // controls outright: the ?url= inspect link and the vanity name.
        //
        // Since these travel as *parameters* of a structured message template rather than as text
        // spliced into it, a CR/LF can no longer forge a record in a structured sink - the value is
        // one field, whatever is in it. What it can still do is forge a *line*: the default console
        // formatter renders the template back into a single line of text, and this app's log is
        // read through `docker compose logs`, so an embedded CR/LF there still produces output that
        // looks like separate entries. Control characters therefore still become '?'.
        //
        // Truncation is independent of all that and outlives any sink change: ?url= is unbounded,
        // and a request-sized value has no business being copied into a log record at all.
        internal static string ForLog(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(empty)";
            }

            var clipped = value.Length > MaxLoggedLength
                ? string.Concat(value.AsSpan(0, MaxLoggedLength), "...(truncated)")
                : value;
            return new string([.. clipped.Select(c => char.IsControl(c) ? '?' : c)]);
        }
    }
}
