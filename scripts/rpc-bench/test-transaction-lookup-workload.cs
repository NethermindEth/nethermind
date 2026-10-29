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
tests.Case("larger deterministic pool retains ordinal rotation and full validation", () =>
{
    var largePath = Path.Combine(tests.DirectoryPath, "large.json");
    var large = TransactionLookupWorkload.PrepareAsync(largePath, Rpc, targetCount: 4096).GetAwaiter().GetResult();
    var second = TransactionLookupWorkload.PrepareAsync(Path.Combine(tests.DirectoryPath, "large-second.json"), Rpc, targetCount: 4096).GetAwaiter().GetResult();
    RpcScriptTests.Assert(JsonNode.DeepEquals(large, second) && large["entries"]!.AsArray().Count == 4096, "large targets not deterministic");
    TransactionLookupWorkload.ValidateAsync(large, Rpc, Path.Combine(tests.DirectoryPath, "large-validation.json"), TransactionLookupWorkload.Hash(largePath)).GetAwaiter().GetResult();
    Reject(() => TransactionLookupWorkload.PrepareAsync(largePath, Rpc).GetAwaiter().GetResult());
});
foreach (var preset in new[] { "transaction", "receipt" })
    tests.Case("method-only fixtures " + preset, () =>
    {
        var config = new YamlMappingNode();
        TransactionLookupWorkload.Fixtures(manifest, tests.DirectoryPath, config, preset);
        var selected = (YamlSequenceNode)config.Children[new YamlScalarNode("calls")];
        RpcScriptTests.Assert(selected.Children.Count == 1 && ((YamlMappingNode)selected.Children[0]).Children[new YamlScalarNode("name")].ToString() == TransactionLookupWorkload.SelectedMethods(preset)[0] + " snapshot", "method isolation lost");
    });
foreach (var (primary, legacy, layout, encoding) in new (string?, string?, string, string)[]
{
    ("0x7fc0", null, "number-prefixed", "compact-no-hashes"),
    (null, "0x7ea0" + new string('0', 64) + "c0", "legacy-hash", "compact-hash-prefixed"),
    ("0x7e80c0", null, "number-prefixed", "compact-hash-prefix-empty"),
    ("0x", "0x7fc0", "legacy-hash", "compact-no-hashes"),
    (null, null, "legacy-hash", "missing-or-empty")
})
    tests.Case("stored receipt encoding probe " + layout + "/" + encoding + "/" + primary, () =>
    {
        var probeManifest = (JsonObject)manifest.DeepClone();
        probeManifest["entries"] = new JsonArray(manifest["entries"]![0]!.DeepClone());
        var probes = new List<string>();
        Task<JsonNode?> Probe(string method, JsonArray parameters)
        {
            RpcScriptTests.Assert(method == "debug_getFromDb" && parameters[0]!.GetValue<string>() == "ReceiptsBlocks", "unexpected raw DB probe");
            var key = parameters[1]!.GetValue<string>(); probes.Add(key);
            return Task.FromResult<JsonNode?>(JsonValue.Create(key.Length == 82 ? primary : legacy));
        }
        var path = Path.Combine(tests.DirectoryPath, "encoding.json");
        TransactionLookupWorkload.QualifyReceiptEncodingAsync(probeManifest, Probe, path, CancellationToken.None).GetAwaiter().GetResult();
        var record = JsonNode.Parse(File.ReadAllText(path))!["records"]![0]!;
        RpcScriptTests.Assert(record["keyLayout"]!.GetValue<string>() == layout && record["encoding"]!.GetValue<string>() == encoding, "encoding classification differs");
        RpcScriptTests.Assert(probes[0] == "0x00000000000026d0" + manifest["entries"]![0]!["blockHash"]!.GetValue<string>()[2..] && probes.Count == (layout == "legacy-hash" ? 2 : 1), "raw key or fallback differs");
    });
var strictManifest = (JsonObject)manifest.DeepClone();
strictManifest["entries"] = new JsonArray(manifest["entries"]![0]!.DeepClone(), manifest["entries"]![1]!.DeepClone());
const string receiptItems = "c4018080c0c4018080c0c4018001c0";
string RawReceipt(JsonNode entry, string? receipts = null) => "0x7eb860" + Hash(Convert.ToInt64(entry["blockNumber"]!.GetValue<string>(), 16) * 10)[2..] +
    Hash(Convert.ToInt64(entry["blockNumber"]!.GetValue<string>(), 16) * 10 + 1)[2..] + Hash(Convert.ToInt64(entry["blockNumber"]!.GetValue<string>(), 16) * 10 + 2)[2..] + (receipts ?? "cf" + receiptItems);
JsonObject CaptureStrict(string? raw = null, bool changedValue = false, string? receipts = null, JsonObject? probeManifest = null)
{
    var path = Path.Combine(tests.DirectoryPath, "strict-encoding.json");
    Task<JsonNode?> Probe(string method, JsonArray parameters)
    {
        string key = parameters[1]!.GetValue<string>();
        JsonNode entry = (probeManifest ?? strictManifest)["entries"]!.AsArray().First(entry => key.EndsWith(entry!["blockHash"]!.GetValue<string>()[2..], StringComparison.Ordinal))!;
        return Task.FromResult<JsonNode?>(JsonValue.Create(raw ?? (changedValue ? RawReceipt(entry, receipts)[..^4] + "02c0" : RawReceipt(entry, receipts))));
    }
    TransactionLookupWorkload.QualifyReceiptEncodingAsync(probeManifest ?? strictManifest, Probe, path, CancellationToken.None, requireHashPrefixed: true).GetAwaiter().GetResult();
    return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
}
var oracle = CaptureStrict();
tests.Case("stationary oracle compares all value bytes by block identity regardless record order", () =>
{
    var actual = CaptureStrict();
    var records = actual["records"]!.AsArray();
    JsonNode first = records[0]!.DeepClone(); records.RemoveAt(0); records.Add(first);
    TransactionLookupWorkload.CompareReceiptEncoding(strictManifest, oracle, actual);
});
foreach (string field in new[] { "headNumber", "headHash", "blockNumber", "blockHash", "keyLayout", "encoding", "byteLength", "prefixHex", "valueSha256", "missing", "extra", "duplicate", "missing-field" })
    tests.Case("stationary oracle rejects " + field, () =>
    {
        var actual = CaptureStrict();
        JsonArray records = actual["records"]!.AsArray();
        if (field.StartsWith("head", StringComparison.Ordinal)) actual[field] = "wrong";
        else if (field == "missing") records.RemoveAt(0);
        else if (field == "extra") records.Add(records[0]!.DeepClone());
        else if (field == "duplicate") records[1] = records[0]!.DeepClone();
        else if (field == "missing-field") records[0]!.AsObject().Remove("valueSha256");
        else records[0]![field] = field == "byteLength" ? JsonValue.Create(1) : JsonValue.Create(field == "valueSha256" ? new string('a', 64) : "wrong");
        Reject(() => TransactionLookupWorkload.CompareReceiptEncoding(strictManifest, oracle, actual));
    });
foreach (string raw in new[] { "0x", "0x7fc0", "0x7e80c0", "0x7ec0", "0x7eb86000c0", "0x7ea0" + new string('0', 64) + "c0", "0x7ea1" + new string('0', 66) + "c0", "0x7eb90060" + new string('0', 192) + "c0", "0x7eb860" + new string('0', 192) + "c0" })
    tests.Case("stationary prefix rejects missing/nohash/malformed/count/target " + raw[..Math.Min(20, raw.Length)], () => Reject(() => CaptureStrict(raw)));
foreach (string outer in new[] { "cf", "ce" })
    tests.Case("stationary oracle rejects changed valid receipt value with identical encoding and prefix: " + outer, () =>
{
    var before = CaptureStrict(receipts: outer + receiptItems);
    var actual = CaptureStrict(changedValue: true, receipts: outer + receiptItems);
    foreach (string field in new[] { "encoding", "byteLength", "prefixHex" })
        RpcScriptTests.Assert(JsonNode.DeepEquals(before["records"]![0]![field], actual["records"]![0]![field]), "same-format fixture changed " + field);
    RpcScriptTests.Assert(!JsonNode.DeepEquals(before["records"]![0]!["valueSha256"], actual["records"]![0]!["valueSha256"]), "full-value digest ignored suffix");
    Reject(() => TransactionLookupWorkload.CompareReceiptEncoding(strictManifest, before, actual));
});
string ListHex(string payload, bool legacy = false)
{
    int length = payload.Length / 2 - (legacy ? 1 : 0);
    string hex = length.ToString("x");
    if (hex.Length % 2 != 0) hex = "0" + hex;
    return length < 56 ? (192 + length).ToString("x2") + payload : (247 + hex.Length / 2).ToString("x2") + hex + payload;
}
foreach (bool legacy in new[] { false, true })
{
    tests.Case("production encoder-derived three compact receipt lists: legacy=" + legacy, () => CaptureStrict(receipts: ListHex(receiptItems, legacy)));
    tests.Case("production missing-slot C0 unit fixture with one stored hash: legacy=" + legacy, () =>
    {
        JsonObject one = (JsonObject)strictManifest.DeepClone();
        JsonNode entry = one["entries"]![0]!.DeepClone();
        entry["blockTransactionCount"] = 1;
        one["entries"] = new JsonArray(entry);
        CaptureStrict("0x7ea0" + entry["txHash"]!.GetValue<string>()[2..] + (legacy ? "c0c0" : "c1c0"), probeManifest: one);
    });
    tests.Case("compact missing C0 slot counted between real receipt lists: legacy=" + legacy, () => CaptureStrict(receipts: ListHex("c4018080c0c0c4018001c0", legacy)));
    foreach (int payloadLength in new[] { 0, 52, 53, 251, 252, 253 })
        tests.Case($"receipt outer short/one-byte/two-byte length boundary payload={payloadLength} legacy={legacy}", () =>
            CaptureStrict(receipts: ListHex(ListHex(new string('0', payloadLength * 2)) + "c0c0", legacy)));
}
foreach (var (name, receipts) in new[] {
    ("scalar item", "c300c0c0"), ("string item", "c380c0c0"), ("string outer", "83c0c0c0"), ("missing items", "c2c0c0"), ("no items", "c0"),
    ("appended scalar", "cf" + receiptItems + "00"), ("appended C0", "cf" + receiptItems + "c0"), ("appended two bytes", "cf" + receiptItems + "c0c0"),
    ("adjusted legacy outer with extra C0", ListHex(receiptItems + "c0", legacy: true)), ("adjusted legacy outer with two extra C0", ListHex(receiptItems + "c0c0", legacy: true)), ("partial item", ListHex("c4018080c0c4018080c0c5018001c0")),
    ("truncated item header", ListHex("c0c0f8")), ("oversized item", ListHex("c0c0f83b00")), ("overstated outer", "d0" + receiptItems), ("two-byte outer gap", "cd" + receiptItems),
    ("noncanonical outer long length", "f80f" + receiptItems), ("outer leading zero length", "f9000f" + receiptItems),
    ("noncanonical item long length", ListHex("f80100c0c0")), ("item leading zero length", ListHex("f9000100c0c0")) })
    tests.Case("strict receipt framing rejects " + name, () => Reject(() => CaptureStrict(receipts: receipts)));
foreach (string guard in new[] { "selected hash", "hash count", "ordinal" })
    tests.Case("strict receipt framing retains hash guard: " + guard, () =>
    {
        JsonObject one = (JsonObject)strictManifest.DeepClone();
        one["entries"] = new JsonArray(strictManifest["entries"]![0]!.DeepClone());
        string raw = RawReceipt(one["entries"]![0]!);
        if (guard == "selected hash") raw = raw[..8] + "f" + raw[9..];
        else one["entries"]![0]![guard == "hash count" ? "blockTransactionCount" : "transactionIndex"] = guard == "hash count" ? JsonValue.Create(2) : JsonValue.Create("0x3");
        Reject(() => CaptureStrict(raw, probeManifest: one));
    });
string failurePath = Path.Combine(tests.DirectoryPath, "strict-encoding.json.failure.json");
tests.Case("first rejected raw receipt is retained and stale success report removed", () =>
{
    CaptureStrict(); File.Delete(failurePath);
    string raw = RawReceipt(strictManifest["entries"]![0]!, "c380c0c0");
    Reject(() => CaptureStrict(raw));
    string first = File.ReadAllText(failurePath);
    JsonNode failure = JsonNode.Parse(first)!;
    RpcScriptTests.Assert(failure["rawHex"]!.GetValue<string>() == raw && failure["keyLayout"]!.GetValue<string>() == "number-prefixed" && failure["txHash"]!.GetValue<string>() == strictManifest["entries"]![0]!["txHash"]!.GetValue<string>(), "first failed probe identity/bytes missing");
    RpcScriptTests.Assert(failure["valueSha256"]!.GetValue<string>() == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(raw[2..]))) && failure["reason"]!.GetValue<string>().Contains("prefix type", StringComparison.Ordinal), "failure digest/reason missing");
    Reject(() => CaptureStrict("0x7fc0"));
    RpcScriptTests.Assert(File.ReadAllText(failurePath) == first && !File.Exists(Path.Combine(tests.DirectoryPath, "strict-encoding.json")), "first failure overwritten or success report retained");
});
foreach (int byteCount in new[] { 262143, 262144, 300001 })
    tests.Case("rejected raw receipt full-hex cap boundary retains length and full SHA: " + byteCount, () =>
{
    File.Delete(failurePath);
    string raw = "0x7f" + new string('0', (byteCount - 1) * 2);
    Reject(() => CaptureStrict(raw));
    JsonNode failure = JsonNode.Parse(File.ReadAllText(failurePath))!;
    RpcScriptTests.Assert(new FileInfo(failurePath).Length < 1048576 && (byteCount == 262143 ? failure["rawHex"]!.GetValue<string>() == raw : failure["rawHex"] is null) && failure["rawHexPrefix"]!.GetValue<string>().Length == 512 && failure["rawHexTail"]!.GetValue<string>().Length == 512 && failure["byteLength"]!.GetValue<int>() == byteCount, "large failure is unbounded or missing context");
    RpcScriptTests.Assert(failure["valueSha256"]!.GetValue<string>() == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(raw[2..]))), "large failure SHA is not full-value");
});
tests.Case("invalid raw hex failure records unknown byte length and digest honestly", () =>
{
    File.Delete(failurePath);
    try { CaptureStrict("0xzz"); }
    catch (FormatException)
    {
        JsonNode failure = JsonNode.Parse(File.ReadAllText(failurePath))!;
        RpcScriptTests.Assert(failure["rawHexPrefix"]!.GetValue<string>() == "0xzz" && failure["byteLength"] is null && failure["valueSha256"] is null, "invalid hex invented decoded bytes");
        return;
    }
    throw new InvalidOperationException("invalid raw hex accepted");
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
tests.Case("snapshot restoration waits for exact pinned head and consecutive stable hash", () =>
{
    foreach (var pinned in new[] { false, true })
    {
        var probes = 0;
        Task<JsonNode?> Progress(string method, JsonArray parameters)
        {
            probes++;
            var number = probes == 1 ? 9999 : 10000;
            var hash = Hash(!pinned && probes == 2 ? 10001 : number);
            return Task.FromResult<JsonNode?>(new JsonObject { ["number"] = "0x" + number.ToString("x"), ["hash"] = hash });
        }
        var stable = TransactionLookupWorkload.WaitForHeadAsync(10000, pinned ? Hash(10000) : null, Progress, CancellationToken.None, interval: TimeSpan.Zero).GetAwaiter().GetResult();
        RpcScriptTests.Assert(probes == (pinned ? 3 : 4) && stable["hash"]!.GetValue<string>() == Hash(10000), "accepted unstable/wrong anchor");
    }
});
tests.Case("snapshot restoration rejects wrong hash, higher head, timeout and cancellation", () =>
{
    foreach (var (number, hash) in new[] { (10000, Hash(10001)), (10001, Hash(10001)), (9999, Hash(9999)) })
    {
        Task<JsonNode?> Head(string method, JsonArray parameters) => Task.FromResult<JsonNode?>(new JsonObject { ["number"] = "0x" + number.ToString("x"), ["hash"] = hash });
        Reject(() => TransactionLookupWorkload.WaitForHeadAsync(10000, Hash(10000), Head, CancellationToken.None, TimeSpan.FromMilliseconds(20), TimeSpan.Zero).GetAwaiter().GetResult());
    }
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    try
    {
        TransactionLookupWorkload.WaitForHeadAsync(10001, null, Rpc, cancellation.Token).GetAwaiter().GetResult();
        throw new InvalidOperationException("Cancelled restoration accepted");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    RpcScriptTests.Assert(TransactionLookupWorkload.Hash(manifestPath) == manifestHash, "readiness changed manifest");
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
    foreach (var preset in new[] { "mixed", "transaction", "receipt" })
    {
        var selected = TransactionLookupWorkload.SelectedMethods(preset);
        var sampled = Summary(10, 20, 0, 0);
        foreach (var key in sampled["metrics"]!.AsObject().Select(pair => pair.Key).ToArray())
        {
            if (TransactionLookupWorkload.Methods.Except(selected).Any(method => key.Contains(method, StringComparison.Ordinal))) sampled["metrics"]!.AsObject().Remove(key);
            else if (selected.Length == 1 && key.StartsWith("http_reqs{", StringComparison.Ordinal)) sampled["metrics"]![key]!["values"]!["count"] = 10;
        }
        TransactionLookupWorkload.CheckSummary(sampled, preset);
        void Samples(int count, string defect = "")
        {
            using var file = File.Create(Path.Combine(tests.DirectoryPath, "samples.gz"));
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress);
            using var writer = new StreamWriter(gzip);
            for (var index = 0; index < count; index++)
            {
                var data = new JsonObject { ["time"] = "2026-10-28T16:00:00Z", ["value"] = .5,
                    ["tags"] = new JsonObject { ["rpc_method"] = selected[index % selected.Length] },
                    ["metadata"] = new JsonObject { ["request_id"] = index.ToString() } };
                if (defect == "time") data["time"] = "bad";
                if (defect == "method") data["tags"]!["rpc_method"] = "eth_unknown";
                if (defect == "latency") data["value"] = -1;
                if (defect == "duplicate") data["metadata"]!["request_id"] = "0";
                if (defect == "empty") data["metadata"]!["request_id"] = "";
                if (defect is "missing-metadata" or "tag-only") data.Remove("metadata");
                if (defect == "missing-id") data["metadata"]!.AsObject().Remove("request_id");
                if (defect is "tag-only" or "both") data["tags"]!["request_id"] = index.ToString();
                if (defect == "null-tag") data["tags"]!["request_id"] = null;
                writer.WriteLine(new JsonObject { ["type"] = "Point", ["metric"] = "http_req_duration", ["data"] = data }.ToJsonString());
            }
        }
        tests.Case("accept complete metadata latency samples " + preset, () =>
        {
            Samples(10);
            TransactionLookupWorkload.CheckSamples(Path.Combine(tests.DirectoryPath, "samples.gz"), sampled, preset);
        });
        foreach (var count in new[] { 0, 9, 11 })
        {
            Samples(count);
            Reject(() => TransactionLookupWorkload.CheckSamples(Path.Combine(tests.DirectoryPath, "samples.gz"), sampled, preset));
        }
        foreach (var defect in new[] { "time", "method", "latency", "duplicate", "empty", "missing-metadata", "missing-id", "tag-only", "both", "null-tag" })
            tests.Case("reject invalid latency sample " + preset + "/" + defect, () =>
            {
                Samples(10, defect);
                Reject(() => TransactionLookupWorkload.CheckSamples(Path.Combine(tests.DirectoryPath, "samples.gz"), sampled, preset));
            });
    }
    var legacy = Summary(10, 20, 0, 0);
    foreach (var metric in legacy["metrics"]!.AsObject().Select(pair => pair.Key).ToArray())
    {
        if (legacy["metrics"]![metric]!["values"] is JsonObject values) legacy["metrics"]![metric] = values.DeepClone();
        if (!metric.StartsWith("http_req_failed", StringComparison.Ordinal)) continue;
        legacy["metrics"]![metric]!.AsObject().Remove("rate");
        legacy["metrics"]![metric]!["value"] = 0;
        legacy["metrics"]![metric]!["passes"] = 0;
        legacy["metrics"]![metric]!["fails"] = metric == "http_req_failed" ? 10 : 5;
    }
    TransactionLookupWorkload.CheckSummary(legacy);
    foreach (var metric in legacy["metrics"]!.AsObject().Select(pair => pair.Key).Where(key => key.StartsWith("http_req_failed", StringComparison.Ordinal)))
    {
        foreach (var value in new JsonNode?[] { JsonValue.Create(.1), null, JsonValue.Create("NaN") })
        {
            var invalid = (JsonObject)legacy.DeepClone();
            invalid["metrics"]![metric]!["value"] = value?.DeepClone();
            Reject(() => TransactionLookupWorkload.CheckSummary(invalid));
        }
        var explicitRate = (JsonObject)legacy.DeepClone();
        explicitRate["metrics"]![metric]!["rate"] = .1;
        Reject(() => TransactionLookupWorkload.CheckSummary(explicitRate));
    }
    var missingCount = (JsonObject)legacy.DeepClone();
    missingCount["metrics"]!["http_reqs"]!.AsObject().Remove("count");
    missingCount["metrics"]!["http_reqs"]!["value"] = 10;
    Reject(() => TransactionLookupWorkload.CheckSummary(missingCount));
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
tests.Case("capture budget rejects unbounded rate, duration and VUs", () =>
{
    TransactionLookupWorkload.CheckSampleBudget("100", "120s", "32");
    foreach (var (rate, duration, vus) in new[] { ("501", "120s", "32"), ("100", "121s", "32"), ("100", "120s", "257"), ("100", "2m", "32"), ("100", "120s", "") })
        Reject(() => TransactionLookupWorkload.CheckSampleBudget(rate, duration, vus));
});
tests.Case("pinned k6 check patched exactly once and unknown upstream fails closed", () =>
{
    var original = "const reqMethod = requestData[2]; const payload = requestData[3]; const data = r.json();\n" + TransactionLookupWorkload.OriginalCheck;
    var patched = TransactionLookupWorkload.Patch(original);
    RpcScriptTests.Assert(patched.Contains(TransactionLookupWorkload.StrictCheck, StringComparison.Ordinal) && TransactionLookupWorkload.Patch(patched) == patched, "patch not idempotent");
    Reject(() => TransactionLookupWorkload.Patch("unknown upstream"));
    Reject(() => TransactionLookupWorkload.Patch(original + original));
    const string import = "import exec from 'k6/execution';";
    const string anchor = "const payload = requestData[3];";
    const string metadata = "exec.vu.metrics.metadata.request_id = requestData[0];";
    var source = import + "\n" + original + "\nconst tags = {\"req_name\": reqName, \"rpc_method\": reqMethod,};";
    var identified = TransactionLookupWorkload.Patch(source, requestIdentity: true);
    RpcScriptTests.Assert(identified == TransactionLookupWorkload.Patch(source).Replace(anchor, anchor + "\n    " + metadata, StringComparison.Ordinal) &&
        TransactionLookupWorkload.Patch(identified, requestIdentity: true) == identified, "request metadata patch changed tags or is not idempotent");
    foreach (var invalid in new[] { original, source.Replace(import, "", StringComparison.Ordinal), source + import,
        source.Replace(anchor, "const payload = changed;", StringComparison.Ordinal), source + anchor,
        source + "\nconst tags = {\"request_id\": requestData[0],};", source + "\nconst tags = {request_id: requestData[0]};",
        identified + "\nconst tags = {\"request_id\": requestData[0],};", identified + "\n" + metadata,
        source + "\n" + metadata })
        Reject(() => TransactionLookupWorkload.Patch(invalid, requestIdentity: true));
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
foreach (var summaryPath in args.SelectMany(path => Directory.Exists(path) ? Directory.GetFiles(path, "summary.json", SearchOption.AllDirectories) : [path]))
    tests.Case("archived summary accepted unchanged: " + summaryPath, () => TransactionLookupWorkload.CheckSummary(JsonNode.Parse(File.ReadAllText(summaryPath))!.AsObject()));
tests.Complete();
