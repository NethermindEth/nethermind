#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include CiWorkflowTests.Helpers.cs
using System.Globalization;
using YamlDotNet.RepresentationModel;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash.");
using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("run-rpc-benchmarks.yml");
YamlMappingNode Step(string name) => workflow.Step("benchmark", name);
string Body(string name) => WorkflowTest.Text(Step(name), "run");
foreach (var enabled in new[] { true, false })
    tests.Case($"checkout ignores parent sparse state enabled={enabled}", () =>
    {
        var fixture = new WorkspaceFixture(tests.DirectoryPath, workflow);
        fixture.Git(fixture.Workspace, "sparse-checkout", "set", "--no-cone", "/scripts/expb/");
        if (!enabled) fixture.Git(fixture.Workspace, "config", "--worktree", "core.sparseCheckout", "false");
        var index = fixture.Git(fixture.Workspace, "ls-files", "-v");
        RpcScriptTests.Assert(index.Contains("S scripts/rpc-bench/StartNode.cs", StringComparison.Ordinal) && fixture.Git(fixture.Workspace, "status", "--porcelain", "--untracked-files=no") == "", "parent must be poisoned invisibly");
        var target = fixture.Prepare();
        fixture.Git(target, "init", "-q");
        fixture.Git(target, "remote", "add", "origin", new Uri(fixture.Workspace).AbsoluteUri);
        fixture.Git(target, "fetch", "--depth=1", "origin", "HEAD");
        fixture.Git(target, "checkout", "--force", "--detach", "FETCH_HEAD");
        fixture.Run(Body("Verify scripts checkout"), target);
        RpcScriptTests.Assert(File.ReadAllText(Path.Combine(target, "scripts/rpc-bench/StartNode.cs")) == "fixture\n" && File.Exists(Path.Combine(target, "Dockerfile")), "checkout incomplete");
        RpcScriptTests.Assert(fixture.Git(target, "rev-parse", "HEAD") == fixture.Git(fixture.Workspace, "rev-parse", "HEAD") && fixture.Git(fixture.Workspace, "ls-files", "-v") == index && !File.Exists(Path.Combine(fixture.Workspace, "scripts/rpc-bench/StartNode.cs")), "checkout changed parent sparse state");
    });
tests.Case("summary survives checkout failure", () =>
{
    var fixture = new WorkspaceFixture(tests.DirectoryPath, workflow);
    var target = fixture.Prepare();
    RpcScriptTests.Assert(Directory.Exists(target) && !Directory.Exists(Path.Combine(target, ".git")), "always steps need a checkout-free working directory");
    fixture.Run(System.Text.RegularExpressions.Regex.Replace(Body("Publish step summary"), @"\$\{\{.*?\}\}", "false"), target);
    RpcScriptTests.Assert(File.ReadAllText(fixture.Environment["GITHUB_STEP_SUMMARY"]).Contains("# RPC Benchmark", StringComparison.Ordinal), "summary missing");
});
foreach (var name in new[] { "Stop reference node and verify DB integrity", "Stop node and verify DB integrity", "Collect perf profile", "Collect dotTrace snapshots", "Collect dotnet-trace", "Upload perf profile", "Upload dotTrace snapshot", "Upload dotnet-trace", "Stage private corpus results", "Defensive cleanup" })
    tests.Case("teardown requires setup: " + name, () =>
    {
        var condition = WorkflowTest.Text(Step(name), "if");
        RpcScriptTests.Assert(condition.Contains("always()", StringComparison.Ordinal) && condition.Contains("steps.scripts.outcome == 'success'", StringComparison.Ordinal), "unguarded script teardown");
        RpcScriptTests.Assert(WorkflowTest.Text(Step("Verify scripts checkout"), "id") == "scripts", "script setup id changed");
        if (name.StartsWith("Stop ", StringComparison.Ordinal)) RpcScriptTests.Assert(condition.Contains($"steps.{(name.Contains("reference", StringComparison.Ordinal) ? "start-reference" : "start-node")}.outcome != 'skipped'", StringComparison.Ordinal), "teardown runs without start");
    });
foreach (var name in new[] { "Reclaim root disk before pulling", "Reclaim root disk after the run" })
    tests.Case("disk reclamation preserves active files: " + name, () =>
    {
        var fixture = new WorkspaceFixture(tests.DirectoryPath, workflow);
        var active = Directory.CreateDirectory(Path.Combine(fixture.Root, "actions-runner/_work/_temp"));
        foreach (var file in new[] { "git-credentials.config", "node.env", "summary.md" }) File.WriteAllText(Path.Combine(active.FullName, file), "active job data\n");
        fixture.Run(fixture.Maintenance(name), fixture.Workspace);
        RpcScriptTests.Assert(active.GetFiles().Length == 3, "maintenance deleted active files");
    });
foreach (var (root, docker, containerd, output, expected) in new[] { (1.5, 968d, 968d, 968d, 0), (1d, 6d, 6d, 6d, 0), (0.9, 968d, 968d, 968d, 1), (8d, 5.9, 100d, 100d, 1), (8d, 100d, 5.9, 100d, 1), (8d, 100d, 100d, 5.9, 1), (7d, 7d, 7d, 7d, 0) })
    tests.Case($"disk capacity root={root} docker={docker} containerd={containerd} output={output}", () =>
    {
        var fixture = new WorkspaceFixture(tests.DirectoryPath, workflow);
        var result = fixture.Run(fixture.Maintenance("Reclaim root disk before pulling", root, docker, containerd, output), fixture.Workspace, expected);
        if (expected != 0) RpcScriptTests.Assert(result.Contains("Insufficient free space at", StringComparison.Ordinal), "disk capacity diagnostic");
    });
foreach (var label in new[] { "reproducible-benchmarks", "reproducible-benchmarks-arm" })
    tests.Case("per-run output isolation " + label, () =>
    {
        var fixture = new WorkspaceFixture(tests.DirectoryPath, workflow);
        fixture.Environment["RUNNER_LABEL"] = label;
        fixture.Prepare();
        var first = fixture.Environment["BENCH_TEMP"];
        fixture.Prepare();
        var second = fixture.Environment["BENCH_TEMP"];
        var parent = fixture.Environment[label.EndsWith("-arm", StringComparison.Ordinal) ? "BENCH_SCRATCH_ROOT" : "RUNNER_TEMP"];
        RpcScriptTests.Assert(Path.GetDirectoryName(first) == parent && Path.GetDirectoryName(second) == parent && first != second, "output placement or isolation changed");
        File.WriteAllText(Path.Combine(first, "other-run.log"), "fixture");
        File.WriteAllText(Path.Combine(second, "profile.zip"), "fixture");
        foreach (var key in new[] { "OUT_DIR", "STATE_DIR" }) RpcScriptTests.Assert(fixture.Environment[key].StartsWith(second + "/", StringComparison.Ordinal), "output escaped selected disk");
        fixture.Run(Body("Remove run output"), fixture.Workspace);
        RpcScriptTests.Assert(!Directory.Exists(second) && File.Exists(Path.Combine(first, "other-run.log")), "cleanup crossed run boundary");
    });
foreach (var name in new[] { "Collect dotTrace snapshots", "Collect perf profile", "Collect dotnet-trace", "Upload dotTrace snapshot", "Upload perf profile", "Upload dotnet-trace", "Upload benchmark results", "Stage private corpus results", "Upload private corpus results", "Scan node logs for errors" })
    tests.Case("large artifact disk: " + name, () =>
    {
        var step = Step(name).ToString();
        RpcScriptTests.Assert(!step.Contains("runner.temp", StringComparison.Ordinal) && !step.Contains("RUNNER_TEMP", StringComparison.Ordinal), "large artifacts use runner temp");
        RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(Step("Check out repository"), "with"), "persist-credentials") == "false", "checkout persists credentials");
    });
foreach (var replacement in new[] { "df() { return 1; }", "df() { printf 'Avail\\ninvalid\\n'; }" })
    tests.Case("failed or malformed df: " + replacement, () =>
    {
        var fixture = new WorkspaceFixture(tests.DirectoryPath, workflow);
        fixture.Run(fixture.Maintenance("Reclaim root disk before pulling").Replace("MIN_FREE_GB=6", "MIN_FREE_GB=6\n" + replacement, StringComparison.Ordinal), fixture.Workspace, 1);
    });
foreach (var (arch, block, expected) in new[] { ("arm64", "", "/data/nethermind/nethermind-flat-25490000"), ("amd64", "", "/mnt/sda/nethermind-flat-snapshot"), ("arm64", "12345", "/data/nethermind/nethermind-flat-12345") })
    tests.Case($"snapshot {arch} {block}", () =>
    {
        var result = workflow.Resolve(new() { ["IN_ARCH"] = arch, ["IN_SNAPSHOT_BLOCK"] = block, ["IN_TOOL"] = "jsonbench", ["IN_DOCKER_IMAGE"] = "nethermindeth/nethermind:master", ["PUSH_BRANCH"] = "master" }, tests.DirectoryPath);
        RpcScriptTests.Assert(result.Code == 0 && result.Values["db_source"] == expected, result.Log);
    });
tests.Complete();

sealed class WorkspaceFixture
{
    internal string Root { get; }
    internal string Workspace { get; }
    internal Dictionary<string, string> Environment { get; }
    private readonly WorkflowTest _workflow;
    internal WorkspaceFixture(string root, WorkflowTest workflow)
    {
        Root = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        Workspace = Directory.CreateDirectory(Path.Combine(Root, "workspace")).FullName;
        _workflow = workflow;
        Environment = new() { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GITHUB_WORKSPACE"] = Workspace,
            ["RUNNER_TEMP"] = Directory.CreateDirectory(Path.Combine(Root, "runner-temp")).FullName, ["GITHUB_ENV"] = Path.Combine(Root, "github-env"),
            ["GITHUB_STEP_SUMMARY"] = Path.Combine(Root, "summary"), ["DOTNET_TRACE"] = "false", ["RUNNER_LABEL"] = "reproducible-benchmarks", ["BENCH_SCRATCH_ROOT"] = Path.Combine(Root, "data disk") };
        Git(Workspace, "init", "-q");
        foreach (var path in new[] { "scripts/expb/SequentialDriver.cs", "scripts/rpc-bench/StartNode.cs", "Dockerfile" })
        {
            var file = Path.Combine(Workspace, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture\n");
        }
        Git(Workspace, "add", ".");
        Git(Workspace, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-qm", "fixture");
    }
    internal string Run(string body, string directory, int expected = 0)
    {
        var result = CiWorkflow.Run(body, Environment, directory);
        RpcScriptTests.Assert(result.Code == expected, result.Output + result.Error);
        return result.Output;
    }
    internal string Git(string directory, params string[] arguments) => Run("git " + string.Join(' ', arguments.Select(CiWorkflow.Quote)), directory).Trim();
    internal string Prepare()
    {
        var setup = _workflow.Step("benchmark", "Set up run paths");
        var steps = ((YamlSequenceNode)WorkflowTest.Get(_workflow.Job("benchmark"), "steps")!).Children.Select(step => WorkflowTest.Text(step, "name")).ToList();
        RpcScriptTests.Assert(WorkflowTest.Text(setup, "working-directory") == "." && steps.IndexOf("Set up run paths") < steps.IndexOf("Check out repository"), "run paths must precede checkout");
        Run(WorkflowTest.Text(setup, "run"), Workspace);
        foreach (var (key, value) in CiWorkflow.Outputs(Environment["GITHUB_ENV"])) Environment[key] = value;
        var path = WorkflowTest.Text(WorkflowTest.Get(_workflow.Step("benchmark", "Check out repository"), "with"), "path");
        var cwd = WorkflowTest.Text(WorkflowTest.Get(WorkflowTest.Get(_workflow.Job("benchmark"), "defaults"), "run"), "working-directory");
        RpcScriptTests.Assert(path.Length > 0 && path != "." && path == cwd, "checkout must have its own script working directory");
        return Path.Combine(Workspace, path);
    }
    internal string Maintenance(string name, double root = 100, double docker = 100, double containerd = 100, double output = 100)
    {
        var dockerRoot = Directory.CreateDirectory(Path.Combine(Root, "docker")).FullName;
        var containerdRoot = Directory.CreateDirectory(Path.Combine(Root, "containerd")).FullName;
        Environment["BENCH_TEMP"] = Path.Combine(Root, "output");
        Environment["SCRATCH_ROOT"] = Path.Combine(Root, "data disk");
        Environment["TEST_DOCKER_ROOT"] = dockerRoot;
        Environment["KEEP_IMAGES"] = "";
        Environment["KEEP_TOOL_CONFIG"] = "{}";
        foreach (var (key, value) in new[] { ("ROOT", root), ("DOCKER", docker), ("CONTAINERD", containerd), ("OUTPUT", output) }) Environment[$"TEST_{key}_BYTES"] = ((long)(value * 1073741824)).ToString(CultureInfo.InvariantCulture);
        var body = WorkflowTest.Text(_workflow.Step("benchmark", name), "run").Replace("/root/actions-runner", CiWorkflow.Quote(Path.Combine(Root, "actions-runner")), StringComparison.Ordinal).Replace("/var/lib/containerd", CiWorkflow.Quote(containerdRoot), StringComparison.Ordinal);
        return """
            docker() { if [[ $1 == info ]]; then echo "$TEST_DOCKER_ROOT"; fi; return 0; }
            df() {
              case "${!#}" in
                /) printf 'Avail\n%s\n' "$TEST_ROOT_BYTES" ;;
                "$BENCH_TEMP") printf 'Avail\n%s\n' "$TEST_OUTPUT_BYTES" ;;
                "$TEST_DOCKER_ROOT") printf 'Avail\n%s\n' "$TEST_DOCKER_BYTES" ;;
                *) printf 'Avail\n%s\n' "$TEST_CONTAINERD_BYTES" ;;
              esac
            }
            du() { :; }; find() { :; }; apt-get() { :; }; journalctl() { :; }
            """ + "\n" + body;
    }
}
