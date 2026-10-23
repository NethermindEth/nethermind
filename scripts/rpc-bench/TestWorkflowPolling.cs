#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ../WorkflowPolling.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using System.Text.Json;
using System.Text.Json.Nodes;

using var tests = new RpcScriptTests();
var now = DateTimeOffset.Parse("2026-10-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
foreach (var status in new[] { "requested", "pending", "queued", "waiting", "in_progress" })
    tests.Case($"discover nonterminal {status} run", () =>
    {
        using var document = JsonDocument.Parse((new JsonObject { ["workflow_runs"] = new JsonArray(new JsonObject { ["id"] = 42, ["head_branch"] = "master", ["status"] = status, ["created_at"] = "2026-10-23T12:00:00Z", ["name"] = "Build test" }) }).ToJsonString());
        RpcScriptTests.Assert(WorkflowPolling.Discover(document.RootElement, "master", "^Build", now.AddMinutes(-1)) == "42", "nonterminal run omitted");
    });
tests.Case("discovery excludes old, completed, other-branch and other-name runs", () =>
{
    using var document = JsonDocument.Parse("""
        {"workflow_runs":[
          {"id":1,"head_branch":"master","status":"queued","created_at":"2026-10-23T11:59:00Z","name":"Build"},
          {"id":2,"head_branch":"master","status":"queued","created_at":"2026-10-23T12:00:00Z","name":"Build"},
          {"id":3,"head_branch":"master","status":"queued","created_at":"2026-10-23T11:58:59Z","name":"Build"},
          {"id":4,"head_branch":"other","status":"queued","created_at":"2026-10-23T12:00:00Z","name":"Build"},
          {"id":5,"head_branch":"master","status":"completed","created_at":"2026-10-23T12:00:00Z","name":"Build"},
          {"id":6,"head_branch":"master","status":"queued","created_at":"2026-10-23T12:00:00Z","name":"Other"}
        ]}
        """);
    RpcScriptTests.Assert(WorkflowPolling.Discover(document.RootElement, "master", "^Build$", now.AddMinutes(-1)) == "2", "wrong run selected");
});
foreach (var conclusion in new[] { "success", "failure", "cancelled", "skipped", "timed_out", "neutral", "action_required" })
    tests.Case($"completion {conclusion}", () =>
    {
        var destination = tests.Write("output", "");
        var calls = new List<string>();
        var environment = new Dictionary<string, string> { ["ORG_NAME"] = "org", ["REPO_NAME"] = "repo", ["REF"] = "master", ["RUN_ID"] = "42", ["MAX_WAIT_MINUTES"] = "1", ["TIMEOUT"] = "1", ["INTERVAL"] = "1", ["GITHUB_OUTPUT"] = destination };
        using var output = new StringWriter(); using var error = new StringWriter();
        var result = WorkflowPolling.RunAsync((path, _) => { calls.Add(path); return Task.FromResult(JsonDocument.Parse((new JsonObject { ["status"] = "completed", ["conclusion"] = conclusion }).ToJsonString())); }, (_, _) => Task.CompletedTask, name => environment.GetValueOrDefault(name, ""), now, output, error, CancellationToken.None).GetAwaiter().GetResult();
        RpcScriptTests.Assert(result == (conclusion == "success" ? 0 : 1) && calls.SequenceEqual(["repos/org/repo/actions/runs/42"]) && File.ReadAllText(destination) == (conclusion == "success" ? "run_id=42\n" : ""), output + error.ToString());
    });
foreach (var discovery in new[] { true, false })
    tests.Case($"{(discovery ? "discovery" : "completion")} deadline", () =>
    {
        var calls = 0; var delays = 0;
        var environment = new Dictionary<string, string> { ["RUN_ID"] = discovery ? "" : "42", ["MAX_WAIT_MINUTES"] = "1", ["TIMEOUT"] = "1", ["INTERVAL"] = "30" };
        using var output = new StringWriter(); using var error = new StringWriter();
        var result = WorkflowPolling.RunAsync((_, _) => { calls++; return Task.FromResult(JsonDocument.Parse(discovery ? "{\"workflow_runs\":[]}" : "{\"status\":\"in_progress\"}")); }, (_, _) => { delays++; return Task.CompletedTask; }, name => environment.GetValueOrDefault(name, ""), now, output, error, CancellationToken.None).GetAwaiter().GetResult();
        RpcScriptTests.Assert(result == 1 && calls == 2 && delays == 1, "deadline changed");
    });
foreach (var message in new[] { "API rate limit exceeded", "Not Found" })
    tests.Case($"API discovery failure {message}", () =>
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var result = WorkflowPolling.RunAsync((_, _) => Task.FromResult(JsonDocument.Parse((new JsonObject { ["message"] = message }).ToJsonString())), (_, _) => throw new InvalidOperationException("unexpected retry"), name => name is "MAX_WAIT_MINUTES" or "TIMEOUT" or "INTERVAL" ? "1" : "", now, output, error, CancellationToken.None).GetAwaiter().GetResult();
        RpcScriptTests.Assert(result == 1 && error.ToString().Contains(message == "Not Found" ? "Invalid input" : message), error.ToString());
    });
tests.Case("cancelled poll does not request", () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    using var output = new StringWriter(); using var error = new StringWriter();
    try
    {
        WorkflowPolling.RunAsync((_, _) => throw new InvalidOperationException("request after cancellation"), (_, _) => Task.CompletedTask, name => name is "MAX_WAIT_MINUTES" or "TIMEOUT" or "INTERVAL" ? "1" : "", now, output, error, cancellation.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("cancellation ignored");
});
tests.Complete();
