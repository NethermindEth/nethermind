#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RpcScriptTests.Helpers.cs

using var tests = new RpcScriptTests();
var first = tests.Write("a.jsonl", "{\"fp\":\"f\",\"method\":\"eth_call\",\"response\":{\"result\":{\"b\":[true,null],\"a\":1}}}\n");
var second = tests.Write("b.jsonl", "{\"fp\":\"f\",\"method\":\"eth_call\",\"response\":{\"result\":{\"a\":2,\"b\":[true,null]}}}\n");
tests.Case("deep comparison retains canonical response fingerprints", () =>
{
    var result = RpcScriptTests.Run("scripts/rpc-bench/DeepCheckCompare.cs", "a=" + first, "b=" + second);
    RpcScriptTests.Assert(result.Code == 1 && result.Output.Contains("a=result#b5dc8b7da0bf  b=result#513ca310c124") && result.Output.Contains("DIVERGENT (disagree): 1") && result.Output.Contains("MALFORMED (any client): 0"), result.ToString());
});
tests.Case("deep comparison ignores object key order", () =>
{
    var reordered = tests.Write("reordered.jsonl", "{\"fp\":\"f\",\"method\":\"eth_call\",\"response\":{\"result\":{\"a\":1,\"b\":[true,null]}}}\n");
    var result = RpcScriptTests.Run("scripts/rpc-bench/DeepCheckCompare.cs", "a=" + first, "b=" + reordered);
    RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("DIVERGENT (disagree): 0"), result.ToString());
});
foreach (var (name, response, expected) in new[]
{
    ("missing", "", "missing"),
    ("malformed", "null", "malformed"),
    ("error", "{\"error\":{\"code\":-32000}}", "error#-32000"),
    ("capture", "{\"_capture_error\":\"connection failed\"}", "malformed#connection failed")
})
    tests.Case($"deep comparison identifies {name}", () =>
    {
        var path = tests.Write(name + ".jsonl", response.Length == 0 ? "" : "{\"fp\":\"f\",\"method\":\"eth_call\",\"response\":" + response + "}\n");
        var result = RpcScriptTests.Run("scripts/rpc-bench/DeepCheckCompare.cs", "a=" + first, "b=" + path);
        RpcScriptTests.Assert(result.Code == 1 && result.Output.Contains("b=" + expected), result.ToString());
    });
tests.Case("all malformed responses still fail when equal", () =>
{
    var path = tests.Write("malformed.jsonl", "{\"fp\":\"f\",\"response\":null}\n");
    var result = RpcScriptTests.Run("scripts/rpc-bench/DeepCheckCompare.cs", "a=" + path, "b=" + path);
    RpcScriptTests.Assert(result.Code == 1 && result.Output.Contains("DIVERGENT (disagree): 0") && result.Output.Contains("MALFORMED (any client): 1"), result.ToString());
});
tests.Case("per-category matrix preserves mixed and isolated metrics", () =>
{
    var summary = tests.Write("summary.md", """
        | latency avg (ms) | 1.25 |
        | latency p50 (ms) | 1.1 |
        | latency p90 (ms) | 4 |
        | latency p99 (ms) | 9 |
        | throughput (req/s) | 90 |
        | checks passed | 99% |
        ### Per method
        | eth/call#one | 1 | 2 | 3 | 4 | 5 | 6 |
        """);
    var manifest = tests.Write("manifest", $"mix|nethermind|20={summary}\niso|eth-call-one|nethermind|100={summary}\n");
    var result = RpcScriptTests.Run("scripts/rpc-bench/PerCategoryMatrix.cs", "@" + manifest);
    RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("| client | rps 20 | rps 100 |") && result.Output.Contains("90 / 99% / 1.25 / 1.10 / 4 / 9") && result.Output.Contains("| eth-call-one | - | 90/9 |") && result.Output.Contains("| eth-call-one | 5 | - |"), result.ToString());
});
tests.Complete();
