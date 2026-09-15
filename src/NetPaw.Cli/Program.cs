using NetPaw;
using NetPaw.Adapters;
using NetPaw.Model;
using NetPaw.Planning;
using NetPaw.Presets;
using NetPaw.Reach;
using NetPaw.Repos;
using NetPaw.Store;

const string Usage = """
    netpaw-cli — static-IP profiles for Windows admins (CLI twin of the NetPaw tray)

      netpaw-cli adapters                         list adapters, current config, VLAN capability
      netpaw-cli list                             list profiles (* = matches the adapter right now)
      netpaw-cli show <profile>                   profile details
      netpaw-cli apply <profile> [-a X] [-n]      apply a profile (-n = dry run, print the plan)
      netpaw-cli dhcp [-a X] [-n]                 switch the adapter to DHCP
      netpaw-cli renew [-a X] [-n]                ipconfig /renew on the adapter (DHCP only)
      netpaw-cli advise [-a X]                    DHCP/static configuration advisory (what is missing, what a renew would fix)
      netpaw-cli scan [--all] [--repos] [--no-dhcp] [-a X]  try DHCP + your profiles (--repos: community/repo profiles too) until one works; --all ranks; restores unless --keep
      netpaw-cli incidents [-n 30]                show the incident log (enable it in settings: repair.incidentLog)
      netpaw-cli reach <ip[/prefix]> [--replace] [-a X] [-n]
                                              make <ip> reachable: pick a covering profile, else a preset,
                                              else add a temporary secondary in the assumed /24
      netpaw-cli preset [query]                   list presets (factory default addresses of gear)
      netpaw-cli preset apply <vendor/model|query> [--replace] [-a X] [-n]
      netpaw-cli capture <name> [-a X]            save the adapter's live config as a profile
      netpaw-cli temp                             list temporary addresses NetPaw added
      netpaw-cli clear-temp                       remove them again
      netpaw-cli vlan <adapter> [id]              query / set the driver VLAN id (0 = untagged)
      netpaw-cli where                            print the config directory (profiles.json etc.)
      netpaw-cli export <file> [--managed]        write profiles as JSON (--managed: only for a profiles.d deployment file)
      netpaw-cli import <file>                    add profiles from a JSON file (same name = replace)
      netpaw-cli policy                           show the effective machine policy (HKLM\SOFTWARE\Policies\NetPaw)
      netpaw-cli repo                             list repositories (trust, entry count, last sync)
      netpaw-cli repo add <url[|keyId:pubkey]> | remove <url> | sync | search <query>
      netpaw-cli pack keygen <dir>                create a signing key pair (private.pem + public.txt)
      netpaw-cli pack sign <index.json> <private.pem>
      netpaw-cli pack verify <index.json> [pubkey-base64]
      netpaw-cli pack build <presets.json> <index.json> [--name N]   turn a presets list into a pack

    Exit codes: 0 ok · 1 a step failed · 2 usage/unknown · 3 not VLAN capable · 4 denied by policy

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
        case "renew":
            return Run(svc, NetPaw.Connectivity.Advisor.PlanRenew(svc.ResolveAdapter(adapterName)), dryRun);
        case "advise":
        {
            var adapter = svc.ResolveAdapter(adapterName);
            var snap = new NetPaw.Connectivity.ConnectivityChecker(new NetPaw.Connectivity.NetworkProbe()).Check(adapter, svc.Settings.Checks).GetAwaiter().GetResult();
            Console.WriteLine($"{adapter.Name}: {NetPaw.Connectivity.Snapshot.Describe(snap.State)}");
            var adv = NetPaw.Connectivity.Advisor.Analyze(snap);
            if (adv.Count == 0) { Console.WriteLine("no findings"); return 0; }
            foreach (var x in adv) Console.WriteLine($"[{x.Severity}] {x.Title} — {x.Text}{(x.CanRenew ? "  (a DHCP renew may fix this: netpaw-cli renew)" : "")}");
            return adv.Any(x => x.Severity == NetPaw.Connectivity.AdvisorySeverity.Error) ? 1 : 0;
        }
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
                var d = svc.ResolvePreset(hit, adapter);
                Console.WriteLine(d.Explanation);
                if (d.Kind is ReachKind.AlreadyReachable) return 0;
                var (plan, outcome) = svc.ApplyPreset(hit, adapter, dryRun, replace ? true : null);
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
        case "repo":
        {
            var sub = argv.Count > 1 ? argv[1].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "list":
                    if (svc.RepoStates.Count == 0) { Console.WriteLine("no repositories configured — try: netpaw-cli repo add https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json"); return 0; }
                    foreach (var st in svc.RepoStates)
                        Console.WriteLine($"{st.Repo.Url}\n    {st.Name,-20} {st.Trust,-13} {st.Count,4} entries  {(st.Fetched is { } f ? "synced " + f.ToString("yyyy-MM-dd HH:mm") : "never synced")}{(st.Repo.FromPolicy ? "  [policy]" : "")}{(st.Repo.Pinned ? "  key " + st.Repo.KeyId : "")}{(st.Error is null ? "" : "\n    ! " + st.Error)}");
                    return 0;
                case "add": if (argv.Count < 3) return Fail("repo add needs a URL"); svc.AddRepo(argv[2]); Console.WriteLine("added; run: netpaw-cli repo sync"); return 0;
                case "remove": if (argv.Count < 3) return Fail("repo remove needs a URL"); svc.RemoveRepo(argv[2]); Console.WriteLine("removed"); return 0;
                case "sync":
                {
                    var states = svc.SyncRepos().GetAwaiter().GetResult();
                    foreach (var st in states) Console.WriteLine($"{(st.Pack is null ? "FAIL" : "ok  ")} {st.Name,-20} {st.Trust,-13} {st.Count,4} entries{(st.Error is null ? "" : "  — " + st.Error)}");
                    return states.All(s => s.Pack is not null) ? 0 : 1;
                }
                case "search":
                {
                    var q = argv.Count > 2 ? argv[2] : "";
                    foreach (var pr in PresetLibrary.Search(svc.Presets, q).Where(p => p.Source is not null)) Console.WriteLine($"preset   {pr.Key,-44} {pr.Ip}/{pr.Prefix,-16} [{pr.Source}]");
                    foreach (var p in svc.RepoProfiles.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"profile  {p.Name,-44} {p.Summary(),-20} [{p.Source}]");
                    return 0;
                }
                default: return Fail("repo: list | add | remove | sync | search");
            }
        }
        case "pack":
        {
            if (argv.Count < 3) return Fail("pack: keygen <dir> | sign <index.json> <private.pem> | verify <index.json> [pubkey] | build <presets.json> <index.json>");
            switch (argv[1].ToLowerInvariant())
            {
                case "keygen":
                {
                    var key = PackSigner.Generate();
                    Directory.CreateDirectory(argv[2]);
                    File.WriteAllText(Path.Combine(argv[2], "private.pem"), key.PrivateKeyPem);
                    File.WriteAllText(Path.Combine(argv[2], "public.txt"), key.PublicKeyBase64);
                    Console.WriteLine($"keyId {key.KeyId}\npublic {key.PublicKeyBase64}\nprivate.pem written to {argv[2]} — keep it out of the repo");
                    return 0;
                }
                case "sign":
                {
                    if (argv.Count < 4) return Fail("pack sign <index.json> <private.pem>");
                    var pack = PackJson.Parse(File.ReadAllText(argv[2]));
                    pack.Updated = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    PackSigner.Sign(pack, File.ReadAllText(argv[3]));
                    File.WriteAllText(argv[2], PackJson.Serialize(pack) + "\n");
                    Console.WriteLine($"signed {pack.Entries.Count} entries with key {pack.Signature!.KeyId}");
                    return 0;
                }
                case "verify":
                {
                    var pack = PackJson.Parse(File.ReadAllText(argv[2]));
                    var bad = PackJson.VerifyHashes(pack);
                    var invalid = pack.Entries.Where(e => string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Vendor) || (e.Kind == "preset" && !NetPaw.Net.IpMath.IsIPv4(e.Ip)) || (e.Kind == "profile" && (e.Profile is null || e.Profile.Validate().Count > 0))).Select(e => e.Id).ToList();
                    var dup = pack.Entries.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    Console.WriteLine($"{pack.Name}: {pack.Entries.Count} entries, {pack.Entries.Count(e => e.Sha256 is not null)} hashed, {(pack.Signature is null ? "unsigned" : "signed by " + pack.Signature.KeyId)}");
                    if (bad.Count > 0) Console.WriteLine("hash mismatch: " + string.Join(", ", bad));
                    if (invalid.Count > 0) Console.WriteLine("invalid entries: " + string.Join(", ", invalid));
                    if (dup.Count > 0) Console.WriteLine("duplicate ids: " + string.Join(", ", dup));
                    var sigOk = argv.Count > 3 ? PackSigner.Verify(pack, argv[3]) : (bool?)null;
                    if (sigOk is { } so) Console.WriteLine(so ? "signature OK" : "signature INVALID");
                    return bad.Count == 0 && invalid.Count == 0 && dup.Count == 0 && sigOk != false ? 0 : 1;
                }
                case "build":
                {
                    if (argv.Count < 4) return Fail("pack build <presets.json> <index.json> [--name N]");
                    var name = Opt("--name") ?? "pack";
                    var presets = System.Text.Json.JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(argv[2]), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
                    var pack = File.Exists(argv[3]) ? PackJson.Parse(File.ReadAllText(argv[3])) : new Pack { Name = name };
                    foreach (var pr in presets)
                    {
                        var id = (pr.Vendor + "-" + pr.Model).ToLowerInvariant().Replace(" ", "-").Replace("/", "-");
                        pack.Entries.RemoveAll(e => e.Id == id);
                        pack.Entries.Add(new PackEntry { Id = id, Vendor = pr.Vendor, Model = pr.Model, Kind = "preset", Ip = pr.Ip, Prefix = pr.Prefix, HostIp = pr.HostIp, Note = pr.Note, Url = pr.Url });
                    }
                    pack.Entries = pack.Entries.OrderBy(e => e.Kind == "profile").ThenBy(e => e.Vendor).ThenBy(e => e.Model).ToList();
                    PackJson.StampHashes(pack); pack.Signature = null;
                    File.WriteAllText(argv[3], PackJson.Serialize(pack) + "\n");
                    Console.WriteLine($"{argv[3]}: {pack.Entries.Count} entries (unsigned — run pack sign)");
                    return 0;
                }
                default: return Fail("pack: keygen | sign | verify | build");
            }
        }
        case "telemetry":
        {
            // Operator tool: mints the project ingest token once. The token is a CI secret; end users never adopt.
            if (argv.Count < 2 || argv[1] != "adopt") return Fail("telemetry adopt [--endpoint URL] [--project netpaw]");
            var endpoint = Opt("--endpoint") ?? NetPaw.Telemetry.TelemetryClient.DefaultEndpoint;
            var project = Opt("--project") ?? "netpaw";
            var version = typeof(NetPawService).Assembly.GetName().Version?.ToString(3) ?? "0";
            var req = NetPaw.Telemetry.TelemetryClient.RequestAdoption(endpoint, project, version).GetAwaiter().GetResult();
            Console.WriteLine($"Confirm code  {req.Code}  in Workflow Manager → Adoptions (repo smol-kitten/netpaw) within 10 minutes. Waiting…");
            var token = NetPaw.Telemetry.TelemetryClient.WaitForToken(endpoint, req, st => Console.Error.WriteLine("  " + st)).GetAwaiter().GetResult();
            if (token is null) return Fail("not confirmed (rejected or expired) — run again");
            Console.WriteLine(token);
            Console.WriteLine("Store it as the GitHub secret NETPAW_TELEMETRY_TOKEN; the next tag build then ships NetPaw-<ver>-telemetry.msi.");
            return 0;
        }
        case "scan":
        {
            var all = Flag("--all"); var keep = Flag("--keep"); var noDhcp = Flag("--no-dhcp"); var repos = Flag("--repos");
            var adapter = svc.ResolveAdapter(adapterName);
            if (!IsElevated()) return Fail("scan applies profiles; run from an elevated prompt");
            var original = svc.Capture("scan-original", adapter);
            var checks = new NetPaw.Connectivity.CheckSettings { Enabled = true, InternetTargets = svc.Settings.Checks.InternetTargets, DnsCheckHost = svc.Settings.Checks.DnsCheckHost };
            var checker = new NetPaw.Connectivity.ConnectivityChecker(new NetPaw.Connectivity.NetworkProbe());
            var scanner = new NetPaw.Scan.NetworkScanner(async (p, ct) =>
            {
                svc.Apply(svc.PlanProfile(p, adapter));
                var deadline = DateTime.UtcNow.AddSeconds(p.Dhcp ? 12 : 2); NetPaw.Adapters.AdapterInfo live;
                do { await Task.Delay(1000, ct); live = svc.GetAdapters().First(a => a.Name == adapter.Name); }
                while (DateTime.UtcNow < deadline && (!live.Up || live.Addresses.All(a => a.Address.StartsWith("169.254."))));
                return await checker.Check(live, checks, ct);
            });
            var cands = NetPaw.Scan.NetworkScanner.Candidates(svc.Profiles, adapter, !noDhcp && svc.Allowed(Capability.Dhcp), repos ? svc.RepoProfiles : null);
            Console.WriteLine($"{cands.Count} candidate(s) on {adapter.Name}");
            var progress = new Progress<NetPaw.Scan.ScanProgress>(pr => { if (pr.Result is { } r) Console.WriteLine($"  {r.Candidate.Name,-28} {r.Verdict,-22} score {r.Score,2}"); else Console.Write($"  trying {pr.Candidate.Name}…\r"); });
            var results = scanner.Run(cands, all ? NetPaw.Scan.ScanMode.RankAll : NetPaw.Scan.ScanMode.FirstWorking, progress).GetAwaiter().GetResult();
            Console.WriteLine("\nranking:");
            foreach (var r in results) Console.WriteLine($"  {r.Score,2}  {r.Candidate.Name,-28} {r.Verdict}");
            var best = results.FirstOrDefault(r => r.Working);
            var live = svc.GetAdapters().First(a => a.Name == adapter.Name);
            if (keep && best is not null) { svc.Apply(svc.PlanProfile(best.Candidate, live)); Console.WriteLine($"kept '{best.Candidate.Name}'"); }
            else { svc.Apply(svc.PlanProfile(original, live)); Console.WriteLine(best is null ? "nothing worked; previous configuration restored" : $"best: '{best.Candidate.Name}' — previous configuration restored (use --keep to apply the winner)"); }
            return best is null ? 1 : 0;
        }
        case "incidents":
        {
            var n = int.TryParse(Opt("-n"), out var k) ? k : 30;
            var log = new NetPaw.Connectivity.IncidentLog(svc.Store.IncidentsFile);
            var items = log.Read(n);
            if (items.Count == 0) { Console.WriteLine(svc.Settings.Repair.IncidentLog ? "no incidents recorded" : "incident log is off (Settings → Incident log)"); return 0; }
            foreach (var i in items) Console.WriteLine(i);
            return 0;
        }
        case "where":
            Console.WriteLine(svc.Store.Directory);
            Console.WriteLine(svc.Machine.Directory + "  (managed, read-only)");
            return 0;
        case "export":
        {
            if (argv.Count < 2) return Fail("export needs a file name");
            var managedOnly = Flag("--managed");
            var list = (managedOnly ? svc.Profiles : svc.UserProfiles).Where(p => !p.Temporary).Select(p => { var c = p.Clone(); if (managedOnly) { c.Id = ""; c.Hotkey = null; } return c; }).ToList();
            File.WriteAllText(argv[1], System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            Console.WriteLine($"wrote {list.Count} profile(s) to {argv[1]}{(managedOnly ? " — drop it into " + svc.Machine.Directory + " on target machines" : "")}");
            return 0;
        }
        case "import":
        {
            if (argv.Count < 2) return Fail("import needs a file name");
            var incoming = System.Text.Json.JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(argv[1]), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            var n = 0;
            foreach (var p in incoming)
            {
                var errs = p.Validate(); if (errs.Count > 0) { Console.Error.WriteLine($"skip '{p.Name}': {string.Join("; ", errs)}"); continue; }
                var i = svc.Profiles.FindIndex(x => !x.Managed && string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { p.Id = svc.Profiles[i].Id; svc.Profiles[i] = p; } else svc.Profiles.Add(p);
                n++;
            }
            svc.SaveProfiles();
            Console.WriteLine($"imported {n} profile(s)");
            return 0;
        }
        case "policy":
        {
            var pol = svc.Policy;
            if (!pol.Any) { Console.WriteLine("no machine policy set"); return 0; }
            foreach (var k in Policy.Keys)
            {
                var v = k switch
                {
                    "WorkAdapter" => pol.WorkAdapter, "PanelHotkey" => pol.PanelHotkey, "ConfirmBeforeApply" => pol.ConfirmBeforeApply?.ToString(),
                    "RepoUrls" => string.Join(" ", pol.RepoUrls), "AllowUserProfiles" => pol.AllowUserProfiles.ToString(), "AllowUserRepos" => pol.AllowUserRepos.ToString(),
                    "AllowReach" => pol.AllowReach.ToString(), "AllowTempAddresses" => pol.AllowTempAddresses.ToString(), "AllowDhcp" => pol.AllowDhcp.ToString(), _ => pol.AllowUnsignedRepos.ToString(),
                };
                Console.WriteLine($"{k,-20} {v,-40} {(pol.IsSet(k) ? "(policy)" : "(default)")}");
            }
            return 0;
        }
        default:
            return Fail($"unknown command '{argv[0]}'\n\n{Usage}");
    }
}
catch (DeniedByPolicyException ex) { Console.Error.WriteLine("netpaw: " + ex.Message); return 4; }
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
    if (outcome.Results.Count == 0) { Console.WriteLine("(nothing to do)"); return 0; }
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
