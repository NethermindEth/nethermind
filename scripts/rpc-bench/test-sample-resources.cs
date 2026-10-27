#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include resource-sampling.helpers.cs
#:include rpc-script-tests.helpers.cs

using System.Text.Json.Nodes;

using var tests = new RpcScriptTests();
var cgroup = Path.Combine(tests.DirectoryPath, "cgroup");
Directory.CreateDirectory(cgroup);
void Write(string name, string text) => File.WriteAllText(Path.Combine(cgroup, name), text);
void Reset()
{
    Write("cpu.stat", "usage_usec 0\nthrottled_usec 0\n");
    Write("memory.current", "0\n"); Write("memory.stat", "anon 0\nfile 0\n");
    Write("memory.peak", "999999999999\n"); Write("io.stat", "8:0 rbytes=0 wbytes=0\n");
    foreach (var name in new[] { "cpu", "io", "memory" }) Write(name + ".pressure", "some avg10=0.00 total=0\n");
}
JsonObject Sample(params Action[] ticks)
{
    var index = 0;
    return ResourceSampling.Sample(cgroup, () =>
    {
        if (index == ticks.Length) return true;
        ticks[index++](); return false;
    }, () => { });
}
tests.Case("throttling is a window delta", () =>
{
    Reset(); Write("cpu.stat", "usage_usec 0\nthrottled_usec 5000000\n");
    var summary = Sample(() => Write("cpu.stat", "usage_usec 0\nthrottled_usec 5000400\n"));
    RpcScriptTests.Assert(summary["cpu_throttled_usec"]!.GetValue<long>() == 400, summary.ToJsonString());
});
tests.Case("memory peak comes from sampled window", () =>
{
    Reset();
    var summary = Sample(() => Write("memory.current", "100"), () => Write("memory.current", "700"), () => Write("memory.current", "300"));
    RpcScriptTests.Assert(summary["memory_peak_bytes"]!.GetValue<long>() == 700 && summary["memory_avg_bytes"]!.GetValue<long>() == 1100 / 3, summary.ToJsonString());
});
tests.Case("process memory is separate from page cache", () =>
{
    Reset();
    var summary = Sample(() => Write("memory.stat", "anon 100\nfile 1000\n"), () => Write("memory.stat", "anon 400\nfile 1200\n"), () => Write("memory.stat", "anon 250\nfile 1100\n"));
    RpcScriptTests.Assert(summary["memory_anon_avg_bytes"]!.GetValue<long>() == 250 && summary["memory_anon_peak_bytes"]!.GetValue<long>() == 400 && summary["memory_file_avg_bytes"]!.GetValue<long>() == 1100, summary.ToJsonString());
});
tests.Case("missing memory stat reports null", () =>
{
    Reset(); File.Delete(Path.Combine(cgroup, "memory.stat"));
    var summary = Sample(() => { });
    RpcScriptTests.Assert(summary["memory_anon_avg_bytes"] is null && summary["memory_anon_peak_bytes"] is null && summary["memory_file_avg_bytes"] is null, summary.ToJsonString());
});
tests.Case("sampling defers per-request rates", () =>
{
    Reset();
    var summary = Sample(() => Write("cpu.stat", "usage_usec 1000\nthrottled_usec 0\n"));
    RpcScriptTests.Assert(summary["requests"]!.GetValue<int>() == 0 && !summary.ContainsKey("cpu_ms_per_request"), summary.ToJsonString());
});
tests.Case("normalization uses delivered counts and replaces stale rates", () =>
{
    var path = tests.Write("resources.json", "{\"cpu_seconds\":45.0,\"cpu_avg_cores\":3.0,\"io_read_bytes\":2000,\"memory_peak_bytes\":1,\"requests\":0}");
    foreach (var requests in new[] { 1000, 500, 0, -1 })
    {
        ResourceSampling.Normalize(path, requests);
        var summary = JsonNode.Parse(File.ReadAllText(path))!;
        RpcScriptTests.Assert(summary["requests"]!.GetValue<int>() == requests, "request count changed");
        if (requests > 0)
            RpcScriptTests.Assert(summary["cpu_ms_per_request"]!.GetValue<double>() == 45000.0 / requests && summary["io_read_bytes_per_request"]!.GetValue<double>() == 2000.0 / requests, summary.ToJsonString());
        else RpcScriptTests.Assert(summary["cpu_ms_per_request"] is null && summary["io_read_bytes_per_request"] is null, "stale rates retained");
    }
});
foreach (var contents in new[] { "{\"not\":\"a summary\"}", "[1]", "not JSON" })
    tests.Case($"invalid sample rejected: {contents}", () =>
    {
        var path = tests.Write("invalid.json", contents);
        try { ResourceSampling.Normalize(path, 10); }
        catch (IOException) { return; }
        throw new InvalidOperationException("invalid sample accepted");
    });
tests.Case("IO and pressure counters use window deltas", () =>
{
    Reset(); Write("io.stat", "8:0 rbytes=100 wbytes=500\n"); Write("cpu.pressure", "some avg10=0.00 total=100\n");
    var summary = Sample(() => { Write("io.stat", "8:0 rbytes=200 wbytes=600\n8:1 rbytes=50 wbytes=25\n"); Write("cpu.pressure", "some avg10=0.00 total=180\n"); });
    RpcScriptTests.Assert(summary["io_read_bytes"]!.GetValue<long>() == 150 && summary["io_write_bytes"]!.GetValue<long>() == 125 && summary["stall_cpu_usec"]!.GetValue<long>() == 80, summary.ToJsonString());
});
tests.Complete();
