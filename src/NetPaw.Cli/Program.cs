using NetPaw;
using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Presets;
using NetPaw.Reach;

const string Usage = """
    netpaw — static-IP profiles for Windows admins (CLI twin of the NetPaw tray)

      netpaw adapters                         list adapters, current config, VLAN capability
      netpaw list                             list profiles (* = matches the adapter right now)
      netpaw show <profile>                   profile details
      netpaw apply <profile> [-a X] [-n]      apply a profile (-n = dry run, print the plan)
      netpaw dhcp [-a X] [-n]                 switch the adapter to DHCP
      netpaw reach <ip[/prefix]> [--replace] [-a X] [-n]
                                              make <ip> reachable: pick a covering profile, else a preset,
                                              else add a temporary secondary in the assumed /24
      netpaw preset [query]                   list presets (factory default addresses of gear)
      netpaw preset apply <vendor/model|query> [--replace] [-a X] [-n]
      netpaw capture <name> [-a X]            save the adapter's live config as a profile
      netpaw temp                             list temporary addresses NetPaw added
      netpaw clear-temp                       remove them again
      netpaw vlan <adapter> [id]              query / set the driver VLAN id (0 = untagged)
      netpaw where                            print the config directory (profiles.json etc.)

    Profiles live in %APPDATA%\NetPaw\profiles.json (override with NETPAW_HOME).
    """;

var argv = args.ToList();
bool Flag(params string[] names) { var i = argv.FindIndex(a => names.Contains(a)); if (i < 0) return false; argv.RemoveAt(i); return true; }
string? Opt(params string[] names) { var i = argv.FindIndex(a => names.Contains(a)); if (i < 0 || i + 1 >= argv.Count) return null; var v = argv[i + 1]; argv.RemoveRange(i, 2); return v; }

var dryRun = Flag("-n", "--dry-run");
var replace = Flag("--replace");
var adapterName = Opt("-a", "--adapter");
if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }

var svc = NetPawService.CreateDefault();
try
{
    switch (argv[0].ToLowerInvariant())
    {
        case "adapters":
        {
            foreach (var a in svc.GetAdapters())
            {
                var v = svc.Vlan.Query(a.Name);
                var vlan = v.Capable ? $"vlan-capable{(v.VlanId is { } id ? $" (id {id})" : "")}" : "no vlan";
                Console.WriteLine($"{(a.Up ? "up  " : "down")} {a.Name,-24} {a.Summary(),-48} {vlan}  [{a.Description}]");
            }
            var work = svc.WorkAdapter();
            Console.WriteLine($"\nwork adapter: {work?.Name ?? "(none)"}{(svc.Settings.WorkAdapter is null ? " (auto)" : "")}");
            return 0;
        }
        case "list":
        {
            var work = svc.WorkAdapter();
            if (svc.Profiles.Count == 0) { Console.WriteLine("no profiles yet — try: netpaw capture \"office\""); return 0; }
            foreach (var p in svc.Profiles)
            {
                var cur = work is not null && ApplyPlanner.Matches(p, work) ? "*" : " ";
                Console.WriteLine($"{cur} {p.Name,-24} {p.Summary(),-48}{(p.Hotkey is null ? "" : "  [" + p.Hotkey + "]")}{(p.Temporary ? "  (temp)" : "")}");
            }
            return 0;
        }
        case "show":
        {
            var p = Need(svc, argv, 1);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        case "apply":
        {
            var p = Need(svc, argv, 1);
            var adapter = svc.ResolveAdapter(adapterName ?? p.Adapter);
            return Run(svc, svc.PlanProfile(p, adapter), dryRun);
        }
        case "dhcp":
            return Run(svc, ApplyPlanner.PlanDhcp(svc.ResolveAdapter(adapterName)), dryRun);
        case "reach":
        {
            if (argv.Count < 2) return Fail("reach needs an address");
            var adapter = svc.ResolveAdapter(adapterName);
            var d = svc.ResolveReach(argv[1], adapter);
            Console.WriteLine(d.Explanation);
            if (d.Kind is ReachKind.Invalid) return 2;
            if (d.Kind is ReachKind.AlreadyReachable) return 0;
            var (plan, outcome) = svc.Reach(d, adapter, dryRun, replace ? true : null);
            return Report(plan, outcome, dryRun);
        }
        case "preset":
        {
            if (argv.Count >= 3 && argv[1] == "apply")
            {
                var q = argv[2];
                var hit = svc.Presets.FirstOrDefault(p => string.Equals(p.Key, q, StringComparison.OrdinalIgnoreCase)) ?? PresetLibrary.Search(svc.Presets, q).ToList() switch
                {
                    [var one] => one,
                    [] => null,
                    var many => throw new InvalidOperationException("ambiguous, matches: " + string.Join(", ", many.Select(p => p.Key))),
                };
                if (hit is null) return Fail($"no preset matches '{q}'");
                var adapter = svc.ResolveAdapter(adapterName);
                var d = svc.ResolveReach(hit.Ip, adapter);
                Console.WriteLine($"{hit}  →  {d.Explanation}");
                if (d.Kind is ReachKind.AlreadyReachable) return 0;
                var (plan, outcome) = svc.Reach(d, adapter, dryRun, replace ? true : null);
                return Report(plan, outcome, dryRun);
            }
            foreach (var p in PresetLibrary.Search(svc.Presets, argv.Count > 1 ? argv[1] : ""))
                Console.WriteLine($"{p.Key,-48} {p.Ip}/{p.Prefix,-18} {p.Note}");
            return 0;
        }
        case "capture":
        {
            if (argv.Count < 2) return Fail("capture needs a profile name");
            var adapter = svc.ResolveAdapter(adapterName);
            var p = svc.Capture(argv[1], adapter);
            var existing = svc.Profiles.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { p.Id = svc.Profiles[existing].Id; svc.Profiles[existing] = p; } else svc.Profiles.Add(p);
            svc.SaveProfiles();
            Console.WriteLine($"saved '{p.Name}': {p.Summary()} on {adapter.Name}");
            return 0;
        }
        case "temp":
            foreach (var t in svc.TempAddresses) Console.WriteLine($"{t.Adapter,-20} {t.Address,-20} {t.Reason,-30} {t.AddedAt:HH:mm}");
            return 0;
        case "clear-temp":
        {
            var o = svc.ClearTemp();
            Console.WriteLine(o is null ? "nothing to clear" : o.Success ? "cleared" : "some removals failed — see log");
            return o is null || o.Success ? 0 : 1;
        }
        case "vlan":
        {
            if (argv.Count < 2) return Fail("vlan needs an adapter name");
            var adapter = svc.ResolveAdapter(argv[1]);
            var v = svc.Vlan.Query(adapter.Name);
            if (argv.Count == 2) { Console.WriteLine(v.Capable ? $"{adapter.Name}: VLAN id {v.VlanId?.ToString() ?? "?"} (driver exposes VlanID)" : $"{adapter.Name}: driver exposes no VlanID property — cannot tag from the OS side{(v.Source is null ? "" : "; " + v.Source)}"); return v.Capable ? 0 : 3; }
            if (!v.Capable) return Fail($"{adapter.Name} is not VLAN capable");
            if (!int.TryParse(argv[2], out var id) || id is < 0 or > 4094) return Fail("VLAN id must be 0..4094");
            var plan = new ApplyPlan { Title = $"vlan {id}", Adapter = adapter.Name };
            plan.Steps.Add(ApplyPlanner.VlanStep(adapter.Name, id));
            return Run(svc, plan, dryRun);
        }
        case "where":
            Console.WriteLine(svc.Store.Directory);
            return 0;
        default:
            return Fail($"unknown command '{argv[0]}'\n\n{Usage}");
    }
}
catch (InvalidOperationException ex) { return Fail(ex.Message); }

static Profile Need(NetPawService svc, List<string> argv, int i)
{
    if (argv.Count <= i) throw new InvalidOperationException("profile name required");
    return svc.FindProfile(argv[i]) ?? throw new InvalidOperationException($"no profile '{argv[i]}'. Known: {string.Join(", ", svc.Profiles.Select(p => p.Name))}");
}

static int Fail(string msg) { Console.Error.WriteLine("netpaw: " + msg); return 2; }

static int Run(NetPawService svc, ApplyPlan plan, bool dryRun)
{
    if (!dryRun && !plan.IsEmpty && OperatingSystem.IsWindows() && !IsElevated())
        return Fail("changing adapter settings needs an elevated prompt (run as Administrator)");
    return Report(plan, dryRun ? null : svc.Apply(plan), dryRun);
}

static int Report(ApplyPlan plan, ApplyOutcome? outcome, bool dryRun)
{
    foreach (var w in plan.Warnings) Console.WriteLine("warn: " + w);
    if (dryRun || outcome is null)
    {
        Console.WriteLine(plan.IsEmpty ? "(nothing to do)" : $"plan '{plan.Title}' on '{plan.Adapter}':\n{plan}");
        return 0;
    }
    foreach (var r in outcome.Results)
        Console.WriteLine($"[{(r.Ok ? "ok" : "FAIL")}] {r.Step.Description}{(r.Ok || string.IsNullOrEmpty(r.Output) ? "" : " — " + r.Output.ReplaceLineEndings(" "))}");
    Console.WriteLine(outcome.Success ? $"applied '{plan.Title}' on {plan.Adapter}" : "FAILED" + (outcome.Aborted ? " (aborted at first critical step)" : ""));
    return outcome.Success ? 0 : 1;
}

static bool IsElevated()
{
    if (!OperatingSystem.IsWindows()) return true;
    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
