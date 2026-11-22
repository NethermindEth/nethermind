#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include ../ci/ci-workflow-tests.helpers.cs

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash.");
using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("sync-master-validation.yml");
var body = CiWorkflow.Bodies(workflow, "Collect failed jobs").Single();
const long reclaim = 1_760_000_000;
const string main = "Sync mainnet (Flat) / sync", gnosis = "Sync gnosis (Flat) / sync";
const string label = "f-1-master-mainnet", gnosisLabel = "f-1-master-gnosis";
const string create = "Sync gnosis (Flat) / create_runner", destroy = "Sync gnosis (Flat) / destroy_runner";
var matrix = """[{"network":"mainnet","mode":"Flat","runner_label":"f-1-master-mainnet"},{"network":"mainnet","mode":"HalfPath","runner_label":"hp-1-master-mainnet"},{"network":"gnosis","mode":"Flat","runner_label":"f-1-master-gnosis"}]""";
var bin = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "bin"));
CiWorkflow.Executable(Path.Combine(bin.FullName, "gh"), """
    for a in "$@"; do
      case "$a" in
        */jobs) [[ ${FAIL_ENDPOINT:-} != jobs ]] || exit 1; exec cat "$JOBS_FIXTURE" ;;
        */artifacts) [[ ${FAIL_ENDPOINT:-} != artifacts ]] || exit 1; exec cat "$ARTIFACTS_FIXTURE" ;;
      esac
    done
    """);
void Check(string name, string[] jobs, string[] markers, bool page, string? failed = null,
    string attempt = "1", string? markerAttempt = null, string kind = "preempted", string endpoint = "", long ended = reclaim + 60, long at = reclaim) =>
    tests.Case(name, () =>
    {
        var output = tests.Write("output", "");
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = bin.FullName + ":" + Environment.GetEnvironmentVariable("PATH"), ["GH_TOKEN"] = "x", ["REPO"] = "o/r", ["RUN_ID"] = "1",
            ["RUN_ATTEMPT"] = attempt, ["FAIL_ENDPOINT"] = endpoint, ["MATRIX"] = matrix, ["GITHUB_OUTPUT"] = output,
            ["GITHUB_STEP_SUMMARY"] = Path.Combine(tests.DirectoryPath, "summary"),
            ["JOBS_FIXTURE"] = tests.Write("jobs", string.Concat(jobs.Select(job => $"{job}\t{ended}\n"))),
            ["ARTIFACTS_FIXTURE"] = tests.Write("artifacts", string.Concat(markers.Select(marker => $"{kind}-{markerAttempt ?? attempt}-{at}-{marker}\n")))
        };
        var result = CiWorkflow.Run(body, environment, workflow.Root);
        RpcScriptTests.Assert(result.Code == 0, result.Output + result.Error);
        var values = CiWorkflow.Outputs(output);
        RpcScriptTests.Assert(values["should_page"] == page.ToString().ToLowerInvariant(), name);
        if (failed is not null) RpcScriptTests.Assert(values["failed_jobs"] == failed, name + " wrong jobs");
        if (!page) RpcScriptTests.Assert(!values.ContainsKey("failed_jobs"), "reclaims should not name failed jobs");
    });
Check("real failure names job", [gnosis], [], true, gnosis);
Check("spot exhausted create", [create], [gnosisLabel], false, kind: "spot-exhausted");
Check("real failure beside spot exhaustion", [create, main], [gnosisLabel], true, main, kind: "spot-exhausted");
Check("spot exhaustion leaves teardown visible", [create, destroy], [gnosisLabel], true, destroy, kind: "spot-exhausted");
Check("earlier spot attempt", [create], [gnosisLabel], true, attempt: "2", markerAttempt: "1", kind: "spot-exhausted");
Check("unknown marker kind", [create], [gnosisLabel], true, kind: "quota-exhausted");
Check("preempted cell", [main], [label], false);
Check("all cells preempted", [main, gnosis], [label, gnosisLabel], false);
Check("real failure beside preemption", [main, gnosis], [label], true, gnosis);
Check("every preempted cell job", [main, "Sync mainnet (Flat) / destroy_runner"], [label], false);
Check("modes are distinct", ["Sync mainnet (HalfPath) / sync"], [label], true, "Sync mainnet (HalfPath) / sync");
Check("unknown label", [gnosis], ["f-9-master-sepolia"], true, gnosis);
Check("empty list pages", [], [], true, "(check run for details)");
Check("empty list with preemption pages", [], [label], true, "(check run for details)");
var longName = "Sync gnosis (Flat) / " + new string('x', 100);
Check("long job truncated", [longName], [], true, longName[..60]);
Check("earlier attempt", [main], [label], true, main, attempt: "2", markerAttempt: "1");
Check("current attempt", [main], [label], false, attempt: "2");
Check("artifact lookup fails", [main], [label], true, main, endpoint: "artifacts");
Check("job lookup fails", [gnosis], [], true, "(check run for details)", endpoint: "jobs");
Check("failure predates reclaim", [main], [label], true, main, ended: reclaim - 600);
Check("failure at reclaim", [main], [label], false, ended: reclaim);
Check("unknown reclaim time", [main], [label], true, at: 0);
Check("unknown job end", [main], [label], true, ended: 0);
tests.Complete();
