#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property DefineConstants=SCRIPT_TEST
#:include RpcScriptTests.Helpers.cs
#:include PrepareEthCallCorpus.cs

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

var tests = 0;
var root = Directory.CreateTempSubdirectory("prepare corpus tests ");
try
{
    foreach (var method in new[] { "eth_call", "debug_traceCall", "trace_call" })
    foreach (var gzip in new[] { false, true }) Check($"selector ranking {method} gzip={gzip}", () =>
    {
        var destination = Destination();
        var records = new[] { Call("0xaaaaaaaa"), Call("0xbbbbbbbb" + new string('f', 80000)), Call("0xaaaaaaaa00"), "{\"method\":\"eth_call\",\"params\":[{\"to\":\"0x1\"}]}" };
        var classes = PrepareEthCallCorpus.Convert(Source(records, gzip), destination, EnvironmentFor(method));
        Equal(string.Join(',', classes.Select(pair => $"{pair.Key}={pair.Value}")), "class_1=2,class_2=1,class_3=1");
        var first = Fixture(destination, "class_1");
        Equal(first[0]!["params"]![0]!["data"]!.ToString(), "0xaaaaaaaa"); Equal(first[1]!["params"]![0]!["data"]!.ToString(), "0xaaaaaaaa00");
        Equal(first.All(record => record!["method"]!.ToString() == method), true);
        if (!OperatingSystem.IsWindows()) Equal((int)File.GetUnixFileMode(Path.Combine(destination, "class_1.json")), 420);
    });
    Check("discard unneeded fields and fallback selector", () =>
    {
        var destination = Destination();
        PrepareEthCallCorpus.Convert(Source(["{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_call\",\"params\":[{\"to\":\"0x1\"}]}", "{\"trace\":\"discarded\",\"method\":\"eth_call\",\"params\":[{\"data\":\"0x2\"},\"0x10\"]}"]), destination, EnvironmentFor());
        var records = Fixture(destination, "class_1"); Equal(records.Count, 2);
        foreach (var record in records) Equal(record!.AsObject().Select(pair => pair.Key).Order().SequenceEqual(new[] { "method", "params" }), true);
        Equal(PrepareEthCallCorpus.Selector(JsonNode.Parse("""[{"data":"","input":"0xABCDEF1200"}]""")!.AsArray()), "0xabcdef12");
    });
    foreach (var (record, message) in new[]
    {
        ("{\"method\":", "invalid JSON"), ("[\"eth_call\",[]]", "record must be a JSON object"),
        ("{\"method\":\"eth_getBalance\",\"params\":[]}", "method must be exactly 'eth_call'"),
        ("{\"method\":\"eth_call\",\"params\":{}}", "params must be a JSON array"),
        ("{\"method\":\"eth_call\",\"params\":[NaN]}", "invalid JSON")
    }) Check($"reject line two {message}", () =>
    {
        var destination = Destination(); var source = Source(["", record]);
        Reject(() => PrepareEthCallCorpus.Convert(source, destination, EnvironmentFor()), $"{source}: line 2:", message);
        Equal(Directory.GetFileSystemEntries(destination).Length, 0);
    });
    Check("empty input", () =>
    {
        var source = Source(["", " \t"]); Reject(() => PrepareEthCallCorpus.Convert(source, Destination(), EnvironmentFor()), "line 0:", "no nonblank JSON records");
    });
    Check("failure cleans temporary output", () =>
    {
        var destination = Destination(); Reject(() => PrepareEthCallCorpus.Convert(Source([Call("0xaaaaaaaa"), "not json"]), destination, EnvironmentFor()), "line 2:");
        Equal(Directory.GetFileSystemEntries(destination).Length, 0);
    });
    Check("many selector classes keep descriptors bounded", () =>
    {
        var destination = Destination();
        var source = Source(Enumerable.Range(0, 100).Select(index => Call($"0x{index:x8}")).ToArray());
        var original = new DescriptorLimit.Limit();
        if (OperatingSystem.IsLinux())
        {
            Equal(DescriptorLimit.Get(7, out original), 0);
            var bounded = original; bounded.Soft = (ulong)Directory.GetFileSystemEntries("/proc/self/fd").Length + 16;
            Equal(DescriptorLimit.Set(7, in bounded), 0);
        }
        try
        {
            var classes = PrepareEthCallCorpus.Convert(source, destination, EnvironmentFor());
            Equal(classes.Count, 100); Equal(classes.Values.Sum(), 100); Equal(Directory.GetFiles(destination, "*.tmp").Length, 0);
            foreach (var path in Directory.GetFiles(destination)) { using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        }
        finally { if (OperatingSystem.IsLinux()) Equal(DescriptorLimit.Set(7, in original), 0); }
    });
    Check("debug overrides and default block", () =>
    {
        var destination = Destination();
        PrepareEthCallCorpus.Convert(Source(["""{"method":"eth_call","params":[{"to":"0x1"},"0x10",{"a":{"balance":"0x1"}}]}""", """{"method":"eth_call","params":[{"to":"0x2"}]}"""]), destination, EnvironmentFor("debug_traceCall"));
        var records = Fixture(destination, "class_1");
        Equal(records[0]!["params"]![2]!["stateOverrides"]!["a"]!["balance"]!.ToString(), "0x1");
        Equal(records[1]!["params"]![1]!.ToString(), "latest"); Equal(records[1]!["params"]![2]!["tracer"]!.ToString(), "callTracer");
    });
    Check("trace_call ordering", () =>
    {
        var destination = Destination();
        PrepareEthCallCorpus.Convert(Source(["""{"method":"eth_call","params":[{"to":"0x1"},"0x10",{"a":{"balance":"0x1"}}]}"""]), destination, EnvironmentFor("trace_call"));
        var parameters = Fixture(destination, "class_1")[0]!["params"]!;
        Equal(parameters[1]!.ToJsonString(), "[\"trace\"]"); Equal(parameters[2]!.ToString(), "0x10"); Equal(parameters[3]!["a"]!["balance"]!.ToString(), "0x1");
    });
    Check("failed rewrite leaves no fixture", () =>
    {
        var destination = Destination();
        Reject(() => PrepareEthCallCorpus.Convert(Source([Call("0xaaaaaaaa"), "{\"method\":\"eth_call\",\"params\":[]}"]), destination, EnvironmentFor("debug_traceCall")), "line 2:");
        Equal(Directory.GetFileSystemEntries(destination).Length, 0);
    });
    Check("unknown tracer writes no fixture", () =>
    {
        var destination = Destination(); Reject(() => PrepareEthCallCorpus.Convert(Source([Call("0xaaaaaaaa")]), destination,
            key => key == "RPC_BENCH_CORPUS_METHOD" ? "debug_traceCall" : key == "RPC_BENCH_CORPUS_TRACER" ? "gethTracer" : null), "unknown tracer");
        Equal(Directory.GetFileSystemEntries(destination).Length, 0);
    });
    Check("CLI success and failure", () =>
    {
        var names = new[] { "RPC_BENCH_CORPUS_METHOD", "RPC_BENCH_CORPUS_TRACER", "RPC_BENCH_CORPUS_TRACE_TYPES" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, EnvironmentFor()(name));
            Equal(RpcScriptTests.Run("scripts/rpc-bench/PrepareEthCallCorpus.cs", Source([Call("0xaaaaaaaa")]), Destination()).Code, 0);
            Equal(RpcScriptTests.Run("scripts/rpc-bench/PrepareEthCallCorpus.cs", Source(["not json"]), Destination()).Code, 1);
        }
        finally { for (var index = 0; index < names.Length; index++) Environment.SetEnvironmentVariable(names[index], previous[index]); }
    });
    Console.WriteLine($"Passed {tests} tests."); return 0;
}
finally { root.Delete(recursive: true); }

void Check(string name, Action test) { test(); Console.WriteLine($"PASS {name}"); tests++; }
static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Reject(Action action, params string[] messages)
{
    try { action(); }
    catch (CorpusParityError error) { foreach (var message in messages) if (!error.Message.Contains(message, StringComparison.Ordinal)) throw new Exception($"Expected {message}, got {error.Message}"); return; }
    throw new Exception("Expected rejection");
}
string Destination() => Path.Combine(root.FullName, $"output-{Guid.NewGuid():N}");
string Source(string[] lines, bool compressed = false)
{
    var path = Path.Combine(root.FullName, $"source-{Guid.NewGuid():N}.jsonl" + (compressed ? ".gz" : ""));
    var text = string.Join('\n', lines) + "\n";
    if (compressed) { using var file = File.Create(path); using var gzip = new GZipStream(file, CompressionMode.Compress); using var writer = new StreamWriter(gzip); writer.Write(text); }
    else File.WriteAllText(path, text);
    return path;
}
static string Call(string data) => $"{{\"method\":\"eth_call\",\"params\":[{{\"to\":\"0x1\",\"data\":\"{data}\"}},\"latest\"]}}";
static JsonArray Fixture(string destination, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(destination, name + ".json")))!.AsArray();
static Func<string, string?> EnvironmentFor(string method = "eth_call") => key => key == "RPC_BENCH_CORPUS_METHOD" ? method : null;

static class DescriptorLimit
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Limit { internal ulong Soft; internal ulong Hard; }
    [DllImport("libc", EntryPoint = "getrlimit")]
    internal static extern int Get(int resource, out Limit limit);
    [DllImport("libc", EntryPoint = "setrlimit")]
    internal static extern int Set(int resource, in Limit limit);
}
