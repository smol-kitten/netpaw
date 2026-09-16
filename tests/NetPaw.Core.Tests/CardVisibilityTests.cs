using System.Text.Json;
using NetPaw.Connectivity;
using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class CardVisibilityTests
{
    [Fact]
    public void VisibilityRulesHideAndShow()
    {
        var c = new InfoCardSettings();
        Assert.True(c.Show("Link", false)); Assert.True(c.Show("Link", true));                  // Always
        Assert.False(c.Show("Wi-Fi", false)); Assert.True(c.Show("Wi-Fi", true));               // OnIssue
        Assert.False(c.Show("Switch", false));                                                  // OnIssue with no issue ever = hidden
        c.Rows["Switch"] = Visibility.Always; Assert.True(c.Show("Switch", false));
        c.Rows["Link"] = Visibility.Never; Assert.False(c.Show("Link", true));
        Assert.Equal(Visibility.Always, c.Mode("Address")); Assert.Equal(Visibility.Never, c.Mode("Link"));
        Assert.Equal(Visibility.Never, c.Mode("unknown-kind"));                                  // not in the catalog: default(Visibility)
        c.ResetToDefaults(); Assert.Empty(c.Rows); Assert.Equal(Visibility.OnIssue, c.AutoPin);
        Assert.Equal(15, InfoCardSettings.Catalog.Count); Assert.Equal(InfoCardSettings.Catalog.Count, InfoCardSettings.Kinds.Distinct().Count());
    }

    [Fact]
    public void ErrorAdviceAlwaysShows()
    {
        var c = new InfoCardSettings();
        Assert.True(c.ShowAdvice(AdvisorySeverity.Error)); Assert.True(c.ShowAdvice(AdvisorySeverity.Warning)); Assert.False(c.ShowAdvice(AdvisorySeverity.Info));
        c.Rows["Advice"] = Visibility.Never;
        Assert.True(c.ShowAdvice(AdvisorySeverity.Error)); Assert.False(c.ShowAdvice(AdvisorySeverity.Warning));
        c.Rows["Advice"] = Visibility.Always;
        Assert.True(c.ShowAdvice(AdvisorySeverity.Info));
    }

    [Fact]
    public void AutoPinModes()
    {
        var c = new InfoCardSettings { AutoPin = Visibility.OnIssue };
        Assert.True(c.ShouldPin(true)); Assert.False(c.ShouldPin(false));
        c.AutoPin = Visibility.Always; Assert.True(c.ShouldPin(false));
        c.AutoPin = Visibility.Never; Assert.False(c.ShouldPin(true));
    }

    [Fact]
    public void StickyAlertsMigratesToAutoPin()
    {
        var old = JsonSerializer.Deserialize<Settings>("""{"Checks":{"StickyAlerts":false}}""", JsonStore.Json)!;
        Assert.Null(old.InfoCard);
        Assert.Equal(Visibility.Never, old.CardSettings().AutoPin); Assert.NotNull(old.InfoCard);
        var sticky = JsonSerializer.Deserialize<Settings>("""{"Checks":{"StickyAlerts":true}}""", JsonStore.Json)!;
        Assert.Equal(Visibility.OnIssue, sticky.CardSettings().AutoPin);
        var explicitNew = JsonSerializer.Deserialize<Settings>("""{"Checks":{"StickyAlerts":true},"InfoCard":{"AutoPin":"Always","Rows":{"Switch":"Always"}}}""", JsonStore.Json)!;
        Assert.Equal(Visibility.Always, explicitNew.CardSettings().AutoPin); Assert.Equal(Visibility.Always, explicitNew.CardSettings().Mode("Switch"));
        var json = JsonSerializer.Serialize(explicitNew, JsonStore.Json);
        Assert.Contains("\"InfoCard\"", json); Assert.Contains("\"Always\"", json);                      // enums as strings survive a round trip
    }
}
