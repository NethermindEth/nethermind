#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false
#:property ExperimentalFileBasedProgramEnableTransitiveDirectives=true
#:package YamlDotNet
#:include checks.helpers.cs
#:include ../ci/ci-workflow-tests.helpers.cs

using YamlDotNet.RepresentationModel;

using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("eez-checks.yml");
var projects = EezChecks.Projects(workflow.Root);
tests.Case("existing_dependency_boundaries", () => EezChecks.ValidateDependencies(projects));
foreach (var (path, expected) in new (string, CheckPlan)[]
{
    ("docs/eez.md", new(false, false, "none")),
    ("src/Nethermind/Nethermind.Evm/README.md", new(false, false, "none")),
    ("src/Nethermind/Nethermind.Evm.Test/VirtualMachineTests.cs", new(true, true, "none")),
    ("src/Nethermind/Nethermind.JsonRpc.Test/EthRpcTests.cs", new(false, false, "none")),
    ("tools/StatsAnalyzer/Directory.Build.props", new(false, false, "none")),
    ("src/Nethermind/tests.props", new(true, true, "none")),
    ("src/Nethermind/Nethermind.JsonRpc.SourceGenerator/Generator.cs", new(true, true, "full")),
    ("src/Nethermind/Nethermind.Analyzers/Analyzer.cs", new(true, true, "full")),
    ("src/Nethermind/Nethermind.Eez.Test/StreamingSessionTests.cs", new(true, true, "none")),
    ("src/Nethermind/Nethermind.Eez/EezPlugin.cs", new(true, true, "full")),
    ("src/Nethermind/Nethermind.Eez.Attester/Program.cs", new(true, true, "full")),
    ("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", new(true, true, "smoke")),
    ("src/Nethermind/Nethermind.Crypto/KzgPolynomialCommitments.cs", new(true, true, "smoke")),
    ("src/Nethermind/Nethermind.State.Flat/StateReader.cs", new(true, true, "smoke")),
    ("src/Nethermind/Nethermind.Runner/Program.cs", new(true, true, "smoke")),
    ("Directory.Packages.props", new(true, true, "full")),
    ("nuget.config", new(true, true, "full")),
    ("scripts/eez/devnet/partitions.py", new(true, true, "full")),
    (".github/workflows/eez-checks.yml", new(true, true, "full")),
})
{
    tests.Case("classify_" + path, () => RpcScriptTests.Assert(EezChecks.Select(projects, [path], false) == expected,
        $"Incorrect checks for {path}."));
}
tests.Case("empty_diff_is_not_applicable", () => RpcScriptTests.Assert(
    EezChecks.Select(projects, [], false) == new CheckPlan(false, false, "none"), "An empty diff must not start a devnet."));
tests.Case("nightly_keeps_full_coverage", () => RpcScriptTests.Assert(
    EezChecks.Select(projects, [], true) == new CheckPlan(true, true, "full"), "Scheduled runs require full coverage."));
tests.Case("mixed_changes_cannot_downgrade_full_coverage", () => RpcScriptTests.Assert(
    EezChecks.Select(projects, ["scripts/eez/devnet/run.sh", "src/Nethermind/Nethermind.Core/Block.cs"], false).Live == "full",
    "A shared dependency must not downgrade an EEZ change."));
tests.Case("new_transitive_dependency_is_detected", () =>
{
    var graph = new Dictionary<string, string[]>(projects, StringComparer.Ordinal);
    const string added = "src/Nethermind/Added/Added.csproj";
    graph["src/Nethermind/Nethermind.Core/Nethermind.Core.csproj"] = [added];
    graph[added] = [];
    RpcScriptTests.Assert(EezChecks.Select(graph, ["src/Nethermind/Added/New.cs"], false).Live == "smoke",
        "New transitive dependencies must not require a manually updated path list.");
});
foreach (var project in new[] { "Nethermind.Core", "Nethermind.Eez.Prove", "Nethermind.Eez.Execution", "Nethermind.Eez.Attester" })
{
    tests.Case("reject_reverse_dependency_" + project, () =>
    {
        var graph = new Dictionary<string, string[]>(projects, StringComparer.Ordinal)
        {
            [$"src/Nethermind/{project}/{project}.csproj"] = ["src/Nethermind/Nethermind.Eez/Nethermind.Eez.csproj"],
        };
        try { EezChecks.ValidateDependencies(graph); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException($"Forbidden dependency accepted for {project}.");
    });
}
var gate = CiWorkflow.Bodies(workflow, "Require every selected check").Single();
foreach (var (required, result) in new[] { (false, "skipped"), (true, "success"), (true, "failure"),
    (true, "cancelled"), (true, "skipped"), (false, "failure") })
{
    foreach (var scope in new[] { "success", "failure", "cancelled", "skipped" })
    {
        tests.Case($"gate_{scope}_{required}_{result}", () =>
        {
            var output = CiWorkflow.Run(gate, new()
            {
                ["SCOPE_RESULT"] = scope,
                ["TESTS_REQUIRED"] = required ? "true" : "false",
                ["TESTS_RESULT"] = result,
                ["LIVE_REQUIRED"] = required ? "smoke" : "none",
                ["LIVE_RESULT"] = result,
            }, workflow.Root);
            var pass = scope == "success" && (required ? result == "success" : result == "skipped");
            RpcScriptTests.Assert((output.Code == 0) == pass, "Required checks must fail closed: " + output.Error);
        });
    }
}
foreach (var lane in new[] { "TESTS", "LIVE" })
{
    foreach (var result in new[] { "failure", "cancelled", "skipped" })
    {
        tests.Case($"gate_independent_{lane}_{result}", () =>
        {
            var environment = new Dictionary<string, string>
            {
                ["SCOPE_RESULT"] = "success",
                ["TESTS_REQUIRED"] = "true",
                ["TESTS_RESULT"] = "success",
                ["LIVE_REQUIRED"] = "full",
                ["LIVE_RESULT"] = "success",
                [lane + "_RESULT"] = result,
            };
            RpcScriptTests.Assert(CiWorkflow.Run(gate, environment, workflow.Root).Code != 0,
                "Each selected lane must independently block success.");
        });
    }
}
foreach (var live in new[] { false, true })
{
    tests.Case("gate_mixed_applicability_" + live, () =>
    {
        var output = CiWorkflow.Run(gate, new()
        {
            ["SCOPE_RESULT"] = "success",
            ["TESTS_REQUIRED"] = live ? "false" : "true",
            ["TESTS_RESULT"] = live ? "skipped" : "success",
            ["LIVE_REQUIRED"] = live ? "smoke" : "none",
            ["LIVE_RESULT"] = live ? "success" : "skipped",
        }, workflow.Root);
        RpcScriptTests.Assert(output.Code == 0, "Mixed required and inapplicable work must be accepted.");
    });
}
tests.Case("parent_forwards_selection_and_preserves_nightly_full", () =>
{
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(workflow.Job("live"), "with"), "selection") ==
        "${{ needs.changes.outputs.live }}", "Live coverage must follow the computed selection.");
    var selection = WorkflowTest.Text(WorkflowTest.Get(workflow.Step("changes", "Select checks from the complete change list"), "env"), "EEZ_FULL");
    RpcScriptTests.Assert(selection.Contains("github.event_name == 'schedule'", StringComparison.Ordinal) &&
        selection.Contains("github.event_name == 'workflow_dispatch'", StringComparison.Ordinal), "Nightly and manual runs require full coverage.");
});
tests.Case("compatibility_and_conformance_share_build_with_complementary_filters", () =>
{
    var child = new WorkflowTest("eez-streaming-tests.yml");
    var compatibility = CiWorkflow.Bodies(child, "Compatibility tests").Single();
    var conformance = CiWorkflow.Bodies(child, "Upstream streaming conformance").Single();
    RpcScriptTests.Assert(compatibility.Contains("FullyQualifiedName!~StreamingConformanceTests", StringComparison.Ordinal) &&
        conformance.Contains("FullyQualifiedName~StreamingConformanceTests", StringComparison.Ordinal), "Selected streaming fixtures must run once, separate from compatibility.");
    var variants = (YamlSequenceNode)WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(child.Job("tests"), "strategy"), "matrix"), "include")!;
    RpcScriptTests.Assert(variants.Children.Select(node => WorkflowTest.Text(node, "variant")).SequenceEqual(
        ["release", "release", "checked"]), "Preserve existing architecture and test variant coverage.");
    var checkedVariant = variants.Children.Single(node => WorkflowTest.Text(node, "variant") == "checked");
    RpcScriptTests.Assert(WorkflowTest.Text(checkedVariant, "build-args") == "-p:DefineConstants=TRACE%3BDEBUG",
        "The checked variant must enable checked build constants.");
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(child.Step("tests", "No-intrinsics compatibility"), "env"), "DOTNET_EnableHWIntrinsic") == "0",
        "The no-intrinsics run must disable hardware intrinsics.");
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(child.Step("tests", "Trie DB compatibility"), "env"), "TEST_USE_TRIE") == "1",
        "The Trie DB run must select the trie backend.");
});
tests.Case("child_workflows_have_no_automatic_triggers", () =>
{
    foreach (var file in new[] { "eez-streaming-tests.yml", "eez-devnet-tests.yml" })
    {
        var events = (YamlMappingNode)WorkflowTest.Get(new WorkflowTest(file).Document, "on")!;
        RpcScriptTests.Assert(events.Children.Keys.All(key => key.ToString() is "workflow_call" or "workflow_dispatch"),
            "Only the parent workflow must schedule automatic EEZ runs.");
    }
});
tests.Case("general_test_matrices_do_not_repeat_eez", () =>
{
    foreach (var file in new[] { "nethermind-tests.yml", "nethermind-tests-checked.yml", "nethermind-tests-trie.yml" })
        RpcScriptTests.Assert(!File.ReadAllText(Path.Combine(workflow.Root, ".github/workflows", file)).Contains("Nethermind.Eez.Test", StringComparison.Ordinal),
            "The selective workflow owns EEZ test variants: " + file);
});
tests.Complete();
