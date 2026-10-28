#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include rpc-script-tests.helpers.cs
#:include transaction-lookup-workload.helpers.cs
#:package YamlDotNet

using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

using var tests = new RpcScriptTests();
var manifestPath = Path.Combine(tests.DirectoryPath, "manifest.json");
var calls = 0;
var head = 10000;
var defect = "";
string Hash(long value) => "0x" + value.ToString("x64");
Task<JsonNode?> Rpc(string method, JsonArray parameters)
{
    calls++;
    if (method == "eth_getBlockByNumber")
    {
        var number = parameters[0]!.GetValue<string>() == "latest" ? head : Convert.ToInt32(parameters[0]!.GetValue<string>(), 16);
        return Task.FromResult<JsonNode?>(new JsonObject { ["number"] = "0x" + number.ToString("x"), ["hash"] = Hash(number), ["transactions"] = new JsonArray(Hash(number * 10), Hash(number * 10 + 1), Hash(number * 10 + 2)) });
    }
    var tx = parameters[0]!.GetValue<string>();
    var ordinal = Convert.ToInt32(tx[2..], 16) % 10;
    var block = Convert.ToInt32(tx[2..], 16) / 10;
    if (defect == "null") return Task.FromResult<JsonNode?>(null);
    if (defect == "error") throw new IOException("RPC error");
    var result = new JsonObject { [method == "eth_getTransactionByHash" ? "hash" : "transactionHash"] = tx, ["blockHash"] = Hash(block), ["blockNumber"] = "0x" + block.ToString("x"), ["transactionIndex"] = "0x" + ordinal.ToString("x"), ["extra"] = "baseline" };
    if (defect.Length > 0) result[defect] = "changed";
    return Task.FromResult<JsonNode?>(result);
}
void Reject(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("Expected closed failure");
}
JsonObject manifest = null!;
tests.Case("generate deterministic fixed manifest and two equal-weight JSONL fixtures", () =>
{
    manifest = TransactionLookupWorkload.PrepareAsync(manifestPath, Rpc).GetAwaiter().GetResult();
    RpcScriptTests.Assert(manifest["entries"]!.AsArray().Count == 256, "wrong size");
    var entries = manifest["entries"]!.AsArray();
    RpcScriptTests.Assert(entries.Take(3).Select(entry => entry!["transactionIndex"]!.GetValue<string>()).SequenceEqual(new[] { "0x0", "0x1", "0x2" }), "ordinal rotation changed");
    var config = new YamlMappingNode { { "rps", "123" }, { "duration", "7s" }, { "seed", "42" } };
    TransactionLookupWorkload.Fixtures(manifest, tests.DirectoryPath, config);
    var fixtureCalls = (YamlSequenceNode)config.Children[new YamlScalarNode("calls")];
    RpcScriptTests.Assert(fixtureCalls.Children.Count == 2 && fixtureCalls.Children.Cast<YamlMappingNode>().All(call => call.Children[new YamlScalarNode("weight")].ToString() == "1"), "wrong mix");
    foreach (var method in TransactionLookupWorkload.Methods)
    {
        var rows = File.ReadAllLines(Path.Combine(tests.DirectoryPath, "rpc-calls", TransactionLookupWorkload.Name, method + ".jsonl"));
        RpcScriptTests.Assert(rows.Length == 256 && rows.Select(row => JsonNode.Parse(row)!["params"]![0]!.GetValue<string>()).SequenceEqual(entries.Select(entry => entry!["txHash"]!.GetValue<string>())), "fixture targets differ");
    }
    RpcScriptTests.Assert(config.Children[new YamlScalarNode("seed")].ToString() == "42", "override changed");
});
var manifestHash = TransactionLookupWorkload.Hash(manifestPath);
var report = Path.Combine(tests.DirectoryPath, "validation.json");
void Validate() => TransactionLookupWorkload.ValidateAsync(manifest, Rpc, report, manifestHash).GetAwaiter().GetResult();
tests.Case("same manifest reused across arms and exhaustive baseline validation", () =>
{
    var previous = calls;
    var reloaded = TransactionLookupWorkload.PrepareAsync(manifestPath, Rpc).GetAwaiter().GetResult();
    RpcScriptTests.Assert(calls == previous && JsonNode.DeepEquals(manifest, reloaded), "reselected targets");
    Validate();
    RpcScriptTests.Assert(JsonNode.Parse(File.ReadAllText(report))!["validatedResponses"]!.GetValue<int>() == 512, "skipped endpoint probes");
});
foreach (var invalid in new[] { "null", "error", "hash", "transactionHash", "blockHash", "blockNumber", "transactionIndex", "extra" })
    tests.Case("reject null/error/mismatch without changing manifest: " + invalid, () =>
    {
        defect = invalid;
        Reject(Validate);
        RpcScriptTests.Assert(TransactionLookupWorkload.Hash(manifestPath) == manifestHash && !JsonNode.Parse(File.ReadAllText(report))!["passed"]!.GetValue<bool>(), "manifest changed or missing failure report");
        defect = "";
    });
tests.Case("head mismatch fails rather than generating a new target set", () =>
{
    head++;
    manifest = TransactionLookupWorkload.PrepareAsync(manifestPath, Rpc).GetAwaiter().GetResult();
    Reject(Validate);
    RpcScriptTests.Assert(TransactionLookupWorkload.Hash(manifestPath) == manifestHash, "head mismatch changed manifest");
    head--;
});
tests.Case("generation fails closed on null and insufficient blocks", () =>
{
    defect = "null";
    var path = Path.Combine(tests.DirectoryPath, "invalid.json");
    Reject(() => TransactionLookupWorkload.PrepareAsync(path, Rpc).GetAwaiter().GetResult());
    defect = ""; head = 65;
    Reject(() => TransactionLookupWorkload.PrepareAsync(path, Rpc).GetAwaiter().GetResult());
    RpcScriptTests.Assert(!File.Exists(path), "partial manifest persisted");
    head = 10000;
});
tests.Case("strict summary requires complete successful HTTP requests and both checks", () =>
{
    JsonObject Summary(int count, int passes, int fails, double rate)
    {
        var summary = new JsonObject { ["metrics"] = new JsonObject { ["http_reqs"] = new JsonObject { ["values"] = new JsonObject { ["count"] = count } }, ["checks"] = new JsonObject { ["values"] = new JsonObject { ["passes"] = passes, ["fails"] = fails } }, ["http_req_failed"] = new JsonObject { ["values"] = new JsonObject { ["rate"] = rate } } } };
        foreach (var method in TransactionLookupWorkload.Methods)
        {
            var tags = "{scenario:client,req_name:" + method + " snapshot}";
            summary["metrics"]!["http_req_duration{req_name:'" + method + " snapshot'}"] = new JsonObject { ["values"] = new JsonObject { ["avg"] = .5 } };
            summary["metrics"]!["http_req_duration" + tags] = new JsonObject { ["values"] = new JsonObject { ["avg"] = .5 } };
            summary["metrics"]!["http_reqs" + tags] = new JsonObject { ["values"] = new JsonObject { ["count"] = count / 2 } };
            summary["metrics"]!["http_req_failed" + tags] = new JsonObject { ["values"] = new JsonObject { ["rate"] = rate } };
        }
        return summary;
    }
    TransactionLookupWorkload.CheckSummary(Summary(10, 20, 0, 0));
    foreach (var method in TransactionLookupWorkload.Methods)
    {
        var missing = Summary(10, 20, 0, 0);
        missing["metrics"]!.AsObject().Remove("http_req_duration{scenario:client,req_name:" + method + " snapshot}");
        Reject(() => TransactionLookupWorkload.CheckSummary(missing));
        var empty = Summary(10, 20, 0, 0);
        empty["metrics"]!["http_req_duration{scenario:client,req_name:" + method + " snapshot}"]!["values"]!["avg"] = 0;
        Reject(() => TransactionLookupWorkload.CheckSummary(empty));
        foreach (var (metric, field, value) in new[] { ("http_reqs", "count", 0d), ("http_reqs", "count", 4d), ("http_req_failed", "rate", .1) })
        {
            var inconsistent = Summary(10, 20, 0, 0);
            inconsistent["metrics"]![metric + "{scenario:client,req_name:" + method + " snapshot}"]!["values"]![field] = value;
            Reject(() => TransactionLookupWorkload.CheckSummary(inconsistent));
        }
    }
    foreach (var summary in new JsonObject[] { Summary(0, 0, 0, 0), Summary(10, 19, 1, 0), Summary(10, 20, 0, .1), Summary(10, 0, 0, 0), [] }) Reject(() => TransactionLookupWorkload.CheckSummary(summary));
});
tests.Case("pinned k6 check patched exactly once and unknown upstream fails closed", () =>
{
    var original = "const reqMethod = requestData[2]; const payload = requestData[3]; const data = r.json();\n" + TransactionLookupWorkload.OriginalCheck;
    var patched = TransactionLookupWorkload.Patch(original);
    RpcScriptTests.Assert(patched.Contains(TransactionLookupWorkload.StrictCheck, StringComparison.Ordinal) && TransactionLookupWorkload.Patch(patched) == patched, "patch not idempotent");
    Reject(() => TransactionLookupWorkload.Patch("unknown upstream"));
    Reject(() => TransactionLookupWorkload.Patch(original + original));
    var javascript = tests.Write("check.js", """
        const assert = require('node:assert/strict');
        function valid(data, reqMethod, hash) {
          const payload = JSON.stringify({params:[hash]});
        """ + TransactionLookupWorkload.StrictCheck + "\n}\n" + """
        const hash = '0x' + 'a'.repeat(64);
        for (const method of ['eth_getTransactionByHash', 'eth_getTransactionReceipt']) {
          const result = {blockHash:hash, blockNumber:'0x10', transactionIndex:'0x1'};
          result[method === 'eth_getTransactionByHash' ? 'hash' : 'transactionHash'] = hash;
          assert.equal(valid({result}, method, hash), true);
          for (const data of [{result:null}, {result:[]}, {}, {error:{},result}, {result:{...result,hash:'wrong',transactionHash:'wrong'}}, {result:{...result,blockHash:null}}])
            assert.equal(Boolean(valid(data,method,hash)), false);
        }
        """);
    var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(javascript);
    using var process = System.Diagnostics.Process.Start(start)!;
    var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    RpcScriptTests.Assert(process.ExitCode == 0, error);
});
tests.Complete();
