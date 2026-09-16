using NetPaw.Model;
using NetPaw.Updates;
using Xunit;

namespace NetPaw.Tests;

public class UpdateTests
{
    const string Latest = """{"tag_name":"v0.8.0","html_url":"https://github.com/smol-kitten/netpaw/releases/tag/v0.8.0","prerelease":false,"draft":false,"body":"## Highlights\n\n- Verified apply everywhere\n- Scored fingerprint"}""";

    [Fact]
    public void UpdateCheckerParsesTag()
    {
        var i = UpdateChecker.Parse(Latest, "0.7.2")!;
        Assert.Equal("0.8.0", i.Version); Assert.EndsWith("/v0.8.0", i.Url); Assert.Equal("Highlights", i.Headline);
        Assert.Null(UpdateChecker.Parse(Latest, "0.8.0"));                                            // same
        Assert.Null(UpdateChecker.Parse(Latest, "0.9.1"));                                            // newer than the release (dev build)
        Assert.Equal("0.8.0", UpdateChecker.Parse(Latest.Replace("v0.8.0", "0.8.0"), "0.7.2")!.Version);   // tag without v
        Assert.Equal("0.8.0", UpdateChecker.Parse(Latest.Replace("v0.8.0", "v0.8.0-rc1"), "0.7.2")!.Version);
        Assert.Null(UpdateChecker.Parse(Latest.Replace("\"prerelease\":false", "\"prerelease\":true"), "0.7.2"));
        Assert.Null(UpdateChecker.Parse(Latest.Replace("\"draft\":false", "\"draft\":true"), "0.7.2"));
        Assert.Null(UpdateChecker.Parse("{\"message\":\"API rate limit exceeded\"}", "0.7.2"));
        Assert.Null(UpdateChecker.Parse("<html>", "0.7.2")); Assert.Null(UpdateChecker.Parse("[]", "0.7.2"));
        Assert.Null(UpdateChecker.Parse(Latest, "garbage"));
    }

    [Fact]
    public async Task UpdateCheckerHonoursPolicy()
    {
        var calls = 0;
        var c = new UpdateChecker((_, _) => { calls++; return Task.FromResult(Latest); }, "0.7.2");
        var s = new Settings();
        Assert.Null(await c.Run(s, allowedByPolicy: false, DateTimeOffset.Now)); Assert.Equal(0, calls); Assert.Null(s.UpdateLastCheck);
        s.UpdateCheck = false;
        Assert.Null(await c.Run(s, allowedByPolicy: true, DateTimeOffset.Now)); Assert.Equal(0, calls);
        s.UpdateCheck = true;
        Assert.NotNull(await c.Run(s, allowedByPolicy: true, DateTimeOffset.Now)); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UpdateCheckerCachesForADay()
    {
        var calls = 0;
        var c = new UpdateChecker((_, _) => { calls++; return Task.FromResult(Latest); }, "0.7.2");
        var s = new Settings();
        var t0 = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
        var first = await c.Run(s, true, t0);
        Assert.Equal("0.8.0", first!.Version); Assert.Equal(1, calls); Assert.Equal(t0, s.UpdateLastCheck); Assert.Equal("0.8.0", s.UpdateLastVersionSeen);
        Assert.Null(await c.Run(s, true, t0.AddHours(23))); Assert.Equal(1, calls);                    // within a day: no request
        Assert.False(UpdateChecker.Due(s, true, t0.AddHours(23))); Assert.True(UpdateChecker.Due(s, true, t0.AddHours(24)));
        Assert.Null(await c.Run(s, true, t0.AddHours(25))); Assert.Equal(2, calls);                    // asked again, same version: no second toast
        var c2 = new UpdateChecker((_, _) => Task.FromResult(Latest.Replace("v0.8.0", "v0.8.1")), "0.7.2");
        Assert.Equal("0.8.1", (await c2.Run(s, true, t0.AddDays(3)))!.Version);                        // a newer one is announced once
        var failing = new UpdateChecker((_, _) => throw new HttpRequestException("offline"), "0.7.2");
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.Run(s, true, t0.AddDays(5)));     // the caller logs; the stamp is set so a dead network is not retried every start
        Assert.Equal(t0.AddDays(5), s.UpdateLastCheck);
    }
}
