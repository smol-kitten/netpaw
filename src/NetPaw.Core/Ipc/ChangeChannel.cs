using NetPaw.Planning;

namespace NetPaw.Ipc;

/// <summary>Where the tray/CLI runs a network change (docs/PLAN-v0.14.md, "Change channel").</summary>
public enum ChannelMode
{
    /// <summary>The service is running: send typed intents over the pipe; it re-plans and applies.</summary>
    Service,
    /// <summary>No service but this process is elevated (portable zip from an admin prompt): apply in-process.</summary>
    Elevated,
    /// <summary>Neither: read-only. Diagnostics, info card and map work; every change is refused.</summary>
    User,
}

/// <summary>
/// The one seam every tray/CLI change goes through. From v0.14 the tray is <c>asInvoker</c> and cannot run
/// netsh itself, so in <see cref="ChannelMode.Service"/> it sends a typed <see cref="ServiceRequest"/> and the
/// service re-plans from validated values (it never receives a command line). A portable, elevated run keeps
/// the in-process path (<see cref="ChannelMode.Elevated"/>); a plain user with no service gets
/// <see cref="ChannelMode.User"/>, where changes are refused with one reason.
///
/// Both arms normalise to <see cref="ChangeResult"/> (carrying an <see cref="ApplyReport"/>), so the caller has
/// one result to surface regardless of mode. The mode detector and the pipe send are injected: the detector so
/// the tray can re-probe the service before each change and the CLI can decide once, the send so dispatch is
/// testable off Windows.
/// </summary>
public sealed class ChangeChannel
{
    readonly Func<ChannelMode> _detect;
    readonly Func<ServiceRequest, ChangeResult> _send;

    public ChangeChannel(Func<ChannelMode> detect, Func<ServiceRequest, ChangeResult>? send = null)
    {
        _detect = detect;
        _send = send ?? (r => ServiceChange.Send(r));
        Mode = _detect();
    }

    /// <summary>The mode decided at the last probe. The tray shows/enables UI from this.</summary>
    public ChannelMode Mode { get; private set; }

    /// <summary>Re-probe the service (the tray does this before each change and every 60 s). Returns the new mode.</summary>
    public ChannelMode Refresh() => Mode = _detect();

    /// <summary>
    /// Run one change. <paramref name="plan"/> is what the elevated path executes and what the caller previews;
    /// in service mode it is not sent — the service builds its own plan from <paramref name="request"/>. The
    /// <paramref name="directWork"/> thunk is the in-process apply (usually verify-and-apply, sometimes a custom
    /// path that tracks temp addresses), run only in elevated mode.
    /// </summary>
    public ChangeResult Run(ServiceRequest request, ApplyPlan plan, Func<ApplyOutcome> directWork) => Mode switch
    {
        ChannelMode.Service => _send(request),
        ChannelMode.Elevated => Direct(plan, directWork),
        _ => ChangeResult.Unreachable("This change needs the NetPaw service (install the MSI), or run the portable copy as administrator."),
    };

    static ChangeResult Direct(ApplyPlan plan, Func<ApplyOutcome> directWork)
    {
        var outcome = directWork();
        var report = ApplyReport.From(plan, outcome);
        var error = outcome.Success ? null
            : outcome.Aborted ? "a required step failed"
            : string.Join("; ", outcome.Failures.Select(f => f.Step.Description));
        return new ChangeResult(outcome.Success, error, report);
    }
}
