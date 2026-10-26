#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include CiWorkflowTests.Helpers.cs

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash.");
using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("run-rpc-benchmarks.yml");
var clear = CiWorkflow.Bodies(workflow, "Clear inherited sparse-checkout state").Single();
var verify = CiWorkflow.Bodies(workflow, "Verify scripts checkout").Single();
var tracked = new[] { "scripts/expb/SequentialDriver.cs", "scripts/rpc-bench/StartNode.cs", "scripts/rpc-bench/RpcBench.Helpers.cs", "scripts/ci/TestExpbWorkflow.cs", "src/Nethermind/Nethermind.Core/Block.cs" };
foreach (var scenario in new[] { "poison hidden from status", "poison restored", "clean unchanged", "no checkout", "partial tree rejected", "stubborn bits removed", "complete tree accepted" })
    tests.Case(scenario, () =>
    {
        var directory = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, scenario));
        var environment = new Dictionary<string, string> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null" };
        (int Code, string Output, string Error) Run(string body) => CiWorkflow.Run(body, environment, directory.FullName);
        string Git(params string[] arguments)
        {
            var result = Run("git -c user.name=t -c user.email=t@t -c commit.gpgsign=false " + string.Join(' ', arguments.Select(CiWorkflow.Quote)));
            RpcScriptTests.Assert(result.Code == 0, result.Output + result.Error);
            return result.Output.Trim();
        }
        if (scenario == "no checkout")
        {
            var result = Run(clear);
            RpcScriptTests.Assert(result.Code == 0, result.Error);
            return;
        }
        Git("init", "-q", "-b", "main");
        var paths = tracked.Concat(scenario == "stubborn bits removed"
            ? Enumerable.Range(0, 2000).Select(index => $"src/Nethermind/Nethermind.Generated/Directory{index / 100}/Generated{index}Source.cs") : []).ToArray();
        foreach (var path in paths)
        {
            var file = Path.Combine(directory.FullName, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture\n");
        }
        Git("add", "-A");
        Git("commit", "-qm", "seed");
        var head = Git("rev-parse", "HEAD");
        if (scenario.StartsWith("poison", StringComparison.Ordinal) || scenario == "partial tree rejected")
            Git("sparse-checkout", "set", "--no-cone", "scripts/expb");
        int Skipped() => Git("ls-files", "-v").Split('\n').Count(line => line.StartsWith('S'));
        if (scenario == "poison hidden from status")
        {
            RpcScriptTests.Assert(!Directory.Exists(Path.Combine(directory.FullName, "scripts/rpc-bench")), "poison must hide RPC scripts");
            RpcScriptTests.Assert(Git("status", "--porcelain") == "" && Skipped() == tracked.Length - 1, "skip-worktree hides missing files");
            return;
        }
        if (scenario == "stubborn bits removed")
        {
            Git(["update-index", "--skip-worktree", .. paths]);
            RpcScriptTests.Assert(Skipped() == paths.Length && Git("ls-files", "-v").Length > 64 * 1024, "fixture must exceed pipe buffer");
            var result = Run("git() { if [[ $1 == sparse-checkout && $2 == disable ]]; then return 0; fi; command git \"$@\"; }\n" + clear);
            RpcScriptTests.Assert(result.Code == 0 && !directory.EnumerateFileSystemInfos().Any(), "fallback must remove poisoned fixture workspace: " + result.Error);
            return;
        }
        var check = scenario is "partial tree rejected" or "complete tree accepted";
        var step = Run(check ? verify : clear);
        RpcScriptTests.Assert(step.Code == (scenario == "partial tree rejected" ? 1 : 0), step.Output + step.Error);
        if (scenario == "partial tree rejected")
            RpcScriptTests.Assert(step.Output.Contains("::error::", StringComparison.Ordinal) && step.Output.Contains("scripts/rpc-bench", StringComparison.Ordinal), "partial checkout diagnostic");
        else
        {
            RpcScriptTests.Assert(tracked.All(path => File.Exists(Path.Combine(directory.FullName, path))), "tracked files must remain present");
            RpcScriptTests.Assert(Skipped() == 0 && Git("rev-parse", "HEAD") == head, "guard preserves HEAD and clears skip-worktree");
            RpcScriptTests.Assert(Run("git config --get core.sparseCheckout").Output.Trim() != "true", "sparse setting remains active");
            RpcScriptTests.Assert(!step.Output.Contains("::error::", StringComparison.Ordinal), "complete checkout must not emit errors");
        }
    });
tests.Complete();
