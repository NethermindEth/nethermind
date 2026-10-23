#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include CiWorkflowTests.Helpers.cs
using System.Text.RegularExpressions;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash.");
using var tests = new RpcScriptTests();
var workflows = new[] { "arm-runner-maintenance.yml", "run-expb-reproducible-benchmarks.yml" }.Select(file => new WorkflowTest(file)).ToArray();
var bodies = workflows.SelectMany(workflow => CiWorkflow.Bodies(workflow, "Reap stale expb overlay mounts")).ToArray();
tests.Case("three overlay copies remain identical", () => RpcScriptTests.Assert(bodies.Length == 3 && bodies.Distinct().Count() == 1, "overlay copies drifted"));
tests.Case("mount table seam remains positional", () => RpcScriptTests.Assert(bodies[0].Contains("MOUNTS=\"${1:-/proc/self/mounts}\"", StringComparison.Ordinal) && !bodies[0].Contains("MOUNTS_FILE", StringComparison.Ordinal), "unsafe environment mount seam"));
tests.Case("holder reap covers all overlay owners", () =>
{
    var filters = workflows.SelectMany(workflow => CiWorkflow.Bodies(workflow, "Reap leftover benchmark containers and networks"))
        .Select(body => FiltersRegex().Match(body).Groups[1].Value).ToArray();
    RpcScriptTests.Assert(filters.Length == 3 && filters.Distinct().Count() == 1, "holder filters drifted");
    foreach (var filter in new[] { "name=expb", "label=expb", "name=rpcbench-", "name=nethermind-rpcbench" })
        RpcScriptTests.Assert(filters[0].Split(' ').Contains(filter), "missing holder filter " + filter);
});
foreach (var (body, index) in bodies.Select((body, index) => (body, index)))
    tests.Case($"overlay copy {index + 1} reaps", () =>
    {
        var fixture = new OverlayFixture(tests.DirectoryPath, body);
        var overlay = fixture.Overlay();
        var result = fixture.Run([OverlayFixture.Mount(overlay)]);
        RpcScriptTests.Assert(result.Output.Contains("unmounted " + overlay.Merged, StringComparison.Ordinal) && result.Remaining.Length == 0, result.Output);
    });
foreach (var root in new[] { "", "/", "//", "/data", "/data/.." })
    tests.Case("reject unsafe data root " + root, () =>
    {
        var fixture = new OverlayFixture(tests.DirectoryPath, bodies[0]);
        var overlay = fixture.Overlay();
        var result = fixture.Run([OverlayFixture.Mount(overlay)], data: root);
        RpcScriptTests.Assert(result.Output.Contains(root.Length == 0 ? "EXPB_DATA_DIR is empty" : "too shallow", StringComparison.Ordinal), result.Output);
        RpcScriptTests.Assert(result.Unmounted.Length == 0 && result.Remaining.Length == 1 && Directory.Exists(overlay.Upper), "unsafe root touched mounts");
    });
foreach (var scenario in new[] { "root", "root slash", "root dot", "traversal", "absolute", "sibling", "escaped scratch", "escaped target" })
    tests.Case("reject unsafe overlay " + scenario, () =>
    {
        var fixture = new OverlayFixture(tests.DirectoryPath, bodies[0]);
        var overlay = fixture.Overlay();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Box, "outside"));
        File.WriteAllText(Path.Combine(outside.FullName, "keep"), "precious");
        var upper = scenario switch
        {
            "root" => fixture.Data, "root slash" => fixture.Data + "/", "root dot" => fixture.Data + "/.",
            "traversal" => fixture.Data + "/work/../../outside", "absolute" => "/etc", "sibling" => fixture.Data + "-elsewhere/upper",
            "escaped scratch" => fixture.Data + "/my\\040dir/upper", _ => overlay.Upper
        };
        var merged = scenario == "escaped target" ? fixture.Data + "/my\\040dir/merged" : overlay.Merged;
        var result = fixture.Run([OverlayFixture.Mount((merged, upper, overlay.Work))]);
        var expected = scenario == "escaped target" ? "refusing escaped overlay mount target" : scenario == "escaped scratch" ? "refusing escaped overlay scratch path" : "refusing overlay scratch path outside";
        RpcScriptTests.Assert(result.Output.Contains(expected, StringComparison.Ordinal), result.Output);
        RpcScriptTests.Assert(File.Exists(Path.Combine(fixture.Data, "snapshot/keep.sst")) && File.Exists(Path.Combine(outside.FullName, "keep")), "guard removed protected files");
        if (scenario == "escaped target")
            RpcScriptTests.Assert(result.Unmounted.Length == 0 && result.Remaining.Length == 1 && Directory.Exists(overlay.Upper) && Directory.Exists(overlay.Work) && !result.Output.Contains("unmounted ", StringComparison.Ordinal), "escaped live target reclaimed");
    });
foreach (var scenario in new[] { "plain", "trailing slash", "lazy fallback", "cannot unmount", "nested", "stacked", "stacked non-overlay", "stacked failure" })
    tests.Case("unmount " + scenario, () =>
    {
        var fixture = new OverlayFixture(tests.DirectoryPath, bodies[0]);
        var first = fixture.Overlay();
        var second = fixture.Overlay(scenario == "nested" ? "work/merged/nested" : "second");
        var mounts = new List<string> { OverlayFixture.Mount(first) };
        if (scenario == "nested") mounts.Add(OverlayFixture.Mount(second));
        if (scenario is "stacked" or "stacked failure") mounts.Add(OverlayFixture.Mount((first.Merged, second.Upper, second.Work)));
        if (scenario == "stacked non-overlay") mounts.Insert(0, OverlayFixture.Mount((first.Merged, second.Upper, second.Work), "tmpfs"));
        var failed = scenario is "cannot unmount" or "stacked failure";
        var result = fixture.Run(mounts.ToArray(), scenario == "trailing slash" ? fixture.Data + "/" : null,
            scenario == "lazy fallback" ? first.Merged : "", failed ? first.Merged : "");
        if (failed)
        {
            RpcScriptTests.Assert(result.Output.Contains("is still mounted", StringComparison.Ordinal) && result.Remaining.Length == mounts.Count, result.Output);
            foreach (var path in new[] { first.Upper, first.Work, second.Upper, second.Work }) RpcScriptTests.Assert(Directory.Exists(path), "failed unmount removed " + path);
        }
        else
        {
            var expected = scenario == "lazy fallback" ? new[] { "0 " + first.Merged, "1 " + first.Merged }
                : scenario == "nested" ? ["0 " + second.Merged, "0 " + first.Merged]
                : scenario is "stacked" or "stacked non-overlay" ? ["0 " + first.Merged, "0 " + first.Merged] : ["0 " + first.Merged];
            RpcScriptTests.Assert(result.Remaining.Length == 0 && result.Unmounted.SequenceEqual(expected), "unmount order/fallback changed: " + string.Join(',', result.Unmounted));
            RpcScriptTests.Assert(!Directory.Exists(first.Upper) && !Directory.Exists(first.Work) && Directory.Exists(first.Merged), "scratch/mountpoint lifecycle changed");
            if (scenario == "stacked") RpcScriptTests.Assert(!Directory.Exists(second.Upper) && !Directory.Exists(second.Work), "second layer not reclaimed");
            if (scenario == "stacked non-overlay") RpcScriptTests.Assert(Directory.Exists(second.Upper) && Directory.Exists(second.Work), "non-overlay scratch removed");
        }
        RpcScriptTests.Assert(File.Exists(Path.Combine(fixture.Data, "snapshot/keep.sst")), "snapshot modified");
    });
foreach (var scenario in new[] { "foreign and Docker", "non-overlay", "drift cap", "no matches" })
    tests.Case("scope " + scenario, () =>
    {
        var fixture = new OverlayFixture(tests.DirectoryPath, bodies[0]);
        var overlay = fixture.Overlay();
        var docker = OverlayFixture.Mount(("/var/lib/docker/overlay2/abc/merged", "/var/lib/docker/x", "/var/lib/docker/y"));
        var foreign = OverlayFixture.Mount(("/srv/other/merged", "/srv/other/upper", "/srv/other/work"));
        var mounts = scenario switch
        {
            "foreign and Docker" => new[] { OverlayFixture.Mount(overlay), docker, foreign },
            "non-overlay" => [OverlayFixture.Mount((fixture.Data + "/bound", fixture.Data + "/x", fixture.Data + "/y"), "ext4")],
            "drift cap" => Enumerable.Range(0, 4000).Select(index => OverlayFixture.Mount(($"/srv/other/merged-{index:D5}", $"/srv/other/upper-{index:D5}", "/srv/other/work"))).ToArray(),
            _ => [foreign, docker]
        };
        var result = fixture.Run(mounts);
        RpcScriptTests.Assert(result.Remaining.SequenceEqual(scenario == "foreign and Docker" ? mounts[1..] : mounts), "foreign mount changed");
        RpcScriptTests.Assert(result.Unmounted.SequenceEqual(scenario == "foreign and Docker" ? new[] { "0 " + overlay.Merged } : []), "foreign unmount attempted");
        if (scenario == "drift cap") RpcScriptTests.Assert(result.Output.Split('\n').Count(line => line.Contains("unmatched overlay:", StringComparison.Ordinal)) == 20, "drift diagnostics unbounded");
        if (scenario == "no matches") RpcScriptTests.Assert(result.Output.Contains("No stale expb overlay mounts under", StringComparison.Ordinal) && result.Output.Contains("unmatched overlay: /srv/other/merged", StringComparison.Ordinal) && !result.Output.Contains("/var/lib/docker", StringComparison.Ordinal), "drift diagnostics changed");
    });
tests.Complete();

sealed class OverlayFixture
{
    internal string Box { get; }
    internal string Data { get; }
    private readonly string _body;
    internal OverlayFixture(string root, string body)
    {
        Box = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        Data = Directory.CreateDirectory(Path.Combine(Box, "expb-data")).FullName;
        Directory.CreateDirectory(Path.Combine(Data, "snapshot"));
        File.WriteAllText(Path.Combine(Data, "snapshot/keep.sst"), "precious");
        _body = body;
    }
    internal (string Merged, string Upper, string Work) Overlay(string name = "work")
    {
        var directory = Path.Combine(Data, name);
        foreach (var leaf in new[] { "merged", "upper", "work" }) Directory.CreateDirectory(Path.Combine(directory, leaf));
        File.WriteAllText(Path.Combine(directory, "upper/dirty.sst"), "cancelled writes");
        return (directory + "/merged", directory + "/upper", directory + "/work");
    }
    internal static string Mount((string Merged, string Upper, string Work) overlay, string type = "overlay") =>
        $"expb-executor-1 {overlay.Merged} {type} rw,relatime,lowerdir=/snapshot,upperdir={overlay.Upper},workdir={overlay.Work} 0 0";
    internal (string Output, string[] Remaining, string[] Unmounted) Run(string[] mounts, string? data = null, string failPlain = "", string failAll = "")
    {
        var table = Path.Combine(Box, "mounts");
        var log = Path.Combine(Box, "umount.log");
        File.WriteAllLines(table, mounts);
        File.WriteAllText(log, "");
        var preamble = "set -- " + CiWorkflow.Quote(table) + "\n" + """
            MOUNTS_TABLE="$1"
            sudo() { "$@"; }
            umount() {
              local lazy=0
              if [[ "$1" == -l ]]; then lazy=1; shift; fi
              local t="$1"
              printf '%s %s\n' "$lazy" "$t" >> "$UMOUNT_LOG"
              case ":${FAIL_ALL:-}:" in *":${t}:"*) return 32 ;; esac
              if [[ "$lazy" -eq 0 ]]; then
                case ":${FAIL_PLAIN:-}:" in *":${t}:"*) return 32 ;; esac
              fi
              awk -v t="$t" '{ l[NR] = $0; if ($2 == t) last = NR }
                END { for (i = 1; i <= NR; i++) if (i != last) print l[i] }' "$MOUNTS_TABLE" > "$MOUNTS_TABLE.new" && mv "$MOUNTS_TABLE.new" "$MOUNTS_TABLE"
            }
            """ + "\n";
        var result = CiWorkflow.Run(preamble + _body, new() { ["UMOUNT_LOG"] = log, ["FAIL_PLAIN"] = failPlain, ["FAIL_ALL"] = failAll, ["EXPB_DATA_DIR"] = data ?? Data }, Box);
        RpcScriptTests.Assert(result.Code == 0, "best-effort reap failed: " + result.Output + result.Error);
        return (result.Output, File.ReadAllLines(table).Where(line => line.Length > 0).ToArray(), File.ReadAllLines(log));
    }
}

partial class Program
{
    [GeneratedRegex(@"(?m)^\s*for filter in (.*); do$")]
    private static partial Regex FiltersRegex();
}
