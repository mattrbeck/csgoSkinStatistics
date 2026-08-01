using CSGOSkinAPI.Models;
using CSGOSkinAPI.Security;
using CSGOSkinAPI.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// Regression tests for the issues found in the security review of the request-handling path.
public class SecurityRegressionTests
{
    // --- vanity validation ---------------------------------------------------------------

    [Theory]
    [InlineData("mattrb\n")]     // .NET's `$` also matches before a trailing newline
    [InlineData("mattrb\r\n")]
    [InlineData("mattrb\r")]
    [InlineData("mattrb\t")]
    [InlineData("mattrb\0")]
    [InlineData("matt\nrb")]
    public void IsValidVanity_RejectsControlCharacters(string vanity)
    {
        // The charset check is what stops a vanity from carrying anything but name characters into
        // the server-side steamcommunity.com fetch and into the logs. `$` let a trailing newline
        // through; the pattern is anchored with \z now.
        Assert.False(SteamProfile.IsValidVanity(vanity));
    }

    [Theory]
    [InlineData("mattrb\n")]
    [InlineData("steamcommunity.com/id/mattrb\n")]
    public void ParseSteamInput_VanityWithTrailingNewline_ReturnsNeither(string input)
    {
        var (steamId64, vanity) = SteamProfile.ParseSteamInput(input);

        Assert.Null(steamId64);
        Assert.Null(vanity);
    }

    [Theory]
    [InlineData("mattrb")]
    [InlineData("a-b_c123")]
    public void IsValidVanity_StillAcceptsRealNames(string vanity)
    {
        Assert.True(SteamProfile.IsValidVanity(vanity));
    }

    // --- log sanitisation ----------------------------------------------------------------
    //
    // These values now travel as *parameters* of a structured message template rather than as text
    // spliced into it, so a CR/LF in one can no longer forge a log record: it is one field of one
    // entry whatever it contains. What it can still forge is a log *line* - the default console
    // formatter renders the template back into a single line, and this app's log is read through
    // `docker compose logs` - so the stripping below is still load-bearing for the sink that is
    // actually deployed. Truncation is independent of the sink: ?url= is unbounded either way.
    // Both halves are pinned together by ParseInspectUrl_LogsTheUrlAsASanitisedField below:
    // deleting ForLog from a call site fails it, and so does splicing the value into the
    // message instead of passing it as a template argument.

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\r\nINFO: forged line")]
    [InlineData("a\0b")]
    public void ForLog_StripsControlCharacters(string value)
    {
        // A logged value carrying CR/LF would let a caller append whole log lines of their own.
        var logged = LogSanitizer.ForLog(value);

        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain('\r', logged);
        Assert.DoesNotContain('\0', logged);
    }

    [Fact]
    public void ForLog_TruncatesLongValues()
    {
        var logged = LogSanitizer.ForLog(new string('x', 5000));

        Assert.True(logged.Length < 300, $"expected a clipped value, got {logged.Length} chars");
        Assert.EndsWith("...(truncated)", logged);
    }

    [Fact]
    public void ForLog_LeavesOrdinaryValuesAlone()
    {
        const string url = "steam://run/730//+csgo_econ_action_preview S123A456D789";
        Assert.Equal(url, LogSanitizer.ForLog(url));
    }

    [Fact]
    public void ForLog_HandlesNullAndEmpty()
    {
        Assert.Equal("(empty)", LogSanitizer.ForLog(null));
        Assert.Equal("(empty)", LogSanitizer.ForLog(""));
    }

    [Theory]
    [InlineData("https://example.com/not-a-link")]
    // The value the whole ForLog/parameterisation pair exists for. A benign URL exercises the
    // parameterisation but would pass just as happily with ForLog deleted from the call site, so
    // it proves only half of what this test is for.
    [InlineData("steam://x\r\nINFO: forged")]
    public void ParseInspectUrl_LogsTheUrlAsASanitisedField(string url)
    {
        // Two things at once, because they are two independent halves of one defence and each is
        // invisible from the other's assertion:
        //
        //   1. the caller's value is an ARGUMENT of the message template, not part of the
        //      message, so no punctuation in it can be read back as message text by a structured
        //      sink. That is what {OriginalFormat} being the bare template proves.
        //   2. the value the call site passes has been through ForLog, so it carries no CR/LF for
        //      the console formatter to render as extra lines. That is what the second case
        //      proves - and deleting ForLog from the call site fails it, which asserting on the
        //      helper in isolation never could.
        var log = new CapturingLogger();

        Assert.Null(InspectLink.ParseInspectUrl(url, log));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Failed to decode URL: {InspectUrl}", entry["{OriginalFormat}"]);
        var logged = Assert.IsType<string>(entry["InspectUrl"]);
        Assert.DoesNotContain('\r', logged);
        Assert.DoesNotContain('\n', logged);
        // Sanitised, not dropped or escaped away: ForLog substitutes each control character
        // rather than removing it, so the operator still sees what was sent, at its real length.
        Assert.Equal(url.Length, logged.Length);
    }

    // --- inspect-link templating ---------------------------------------------------------

    [Fact]
    public void BuildInspectLink_OverlongPropertyId_DoesNotThrow()
    {
        // int.Parse on a digit run wider than int would have thrown OverflowException, failing the
        // whole /api/inventory request with a 500 instead of just leaving the placeholder unfilled.
        var props = new List<SteamAssetProperty> { new() { propertyid = 6, string_value = "00AB" } };

        var link = InspectLink.BuildInspectLink(
            "steam://run/730//+csgo_econ_action_preview %propid:99999999999999999999%",
            props, "76561198123456789", "519");

        Assert.Equal(
            "steam://run/730//+csgo_econ_action_preview %propid:99999999999999999999%", link);
    }
}
