using NetPaw.Ipc;
using NetPaw.Planning;
using Xunit;

namespace NetPaw.Tests;

/// <summary>The tray/CLI change channel (docs/PLAN-v0.14.md, "Change channel"): one seam, three modes. Service
/// sends the typed intent and never runs anything locally; elevated runs the in-process apply and reports it;
/// user refuses every change with one reason and runs nothing. The mode is whatever the injected detector last
/// said, and <see cref="ChangeChannel.Refresh"/> re-probes it.</summary>
public class ChangeChannelTests
{
    static ApplyPlan Plan()
    {
        var p = new ApplyPlan { Title = "lab", Adapter = "Ethernet" };
        p.Steps.Add(new Step("set address", "netsh", "interface ip set address ..."));
        return p;
    }

    static ServiceRequest Req() => ServiceChange.Dhcp("Ethernet");

    [Fact]
    public void Service_mode_sends_the_intent_and_never_runs_locally()
    {
        ServiceRequest? sent = null;
        var ran = false;
        var ch = new ChangeChannel(() => ChannelMode.Service, r => { sent = r; return new ChangeResult(true, null, null); });

        var result = ch.Run(Req(), Plan(), () => { ran = true; return new ApplyOutcome(); });

        Assert.True(result.Ok);
        Assert.NotNull(sent);
        Assert.Equal(RequestKind.Dhcp, sent!.Kind);
        Assert.False(ran);   // the local apply must not run in service mode
    }

    [Fact]
    public void Elevated_mode_runs_the_local_apply_and_reports_it()
    {
        var sendCalled = false;
        var ch = new ChangeChannel(() => ChannelMode.Elevated, _ => { sendCalled = true; return new ChangeResult(true, null, null); });

        var outcome = new ApplyOutcome();
        outcome.Results.Add(new StepResult(new Step("set address", "netsh", "...set..."), 0, "Ok."));
        var result = ch.Run(Req(), Plan(), () => outcome);

        Assert.False(sendCalled);           // elevated never touches the pipe
        Assert.True(result.Ok);
        Assert.NotNull(result.Report);      // the direct path still produces an apply report to surface
        Assert.Equal("lab", result.Report!.Title);
    }

    [Fact]
    public void Elevated_mode_surfaces_a_failed_critical_step_as_an_error()
    {
        var ch = new ChangeChannel(() => ChannelMode.Elevated, _ => throw new Xunit.Sdk.XunitException("must not send"));

        var outcome = new ApplyOutcome { Aborted = true };
        outcome.Results.Add(new StepResult(new Step("set address", "netsh", "...set...", Critical: true), 1, "Access is denied."));
        var result = ch.Run(Req(), Plan(), () => outcome);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.NotNull(result.Report);
    }

    [Fact]
    public void User_mode_refuses_without_running_anything()
    {
        var ran = false;
        var ch = new ChangeChannel(() => ChannelMode.User, _ => throw new Xunit.Sdk.XunitException("must not send"));

        var result = ch.Run(Req(), Plan(), () => { ran = true; return new ApplyOutcome(); });

        Assert.False(result.Ok);
        Assert.False(ran);
        Assert.Null(result.Report);
        Assert.Contains("service", result.Error);
    }

    [Fact]
    public void Refresh_re_probes_the_detector()
    {
        var mode = ChannelMode.User;
        var ch = new ChangeChannel(() => mode);
        Assert.Equal(ChannelMode.User, ch.Mode);
        mode = ChannelMode.Service;
        Assert.Equal(ChannelMode.Service, ch.Refresh());
        Assert.Equal(ChannelMode.Service, ch.Mode);
    }
}
