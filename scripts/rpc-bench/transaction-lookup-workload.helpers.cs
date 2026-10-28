// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

internal static class TransactionLookupWorkload
{
    internal const string Name = "transaction-lookups-snapshot";
    internal const string Version = "lookup-v2";
    internal const int Count = 256;
    internal static readonly string[] Methods = ["eth_getTransactionByHash", "eth_getTransactionReceipt"];
    internal const string OriginalCheck = "return data !== undefined && data.result !== undefined && data.error === undefined;";
    internal const string StrictCheck = """
        const expected = JSON.parse(payload).params[0];
                  return data !== undefined && data !== null && data.error === undefined &&
                    data.result !== null && typeof data.result === 'object' && !Array.isArray(data.result) &&
                    (reqMethod === 'eth_getTransactionByHash' ? data.result.hash : data.result.transactionHash) === expected &&
                    typeof data.result.blockHash === 'string' && /^0x[0-9a-fA-F]{64}$/.test(data.result.blockHash) &&
                    typeof data.result.blockNumber === 'string' && /^0x[0-9a-fA-F]+$/.test(data.result.blockNumber) &&
                    typeof data.result.transactionIndex === 'string' && /^0x[0-9a-fA-F]+$/.test(data.result.transactionIndex);
        """;

    internal static string[] SelectedMethods(string preset) => preset switch
    {
        "mixed" => Methods,
        "transaction" => [Methods[0]],
        "receipt" => [Methods[1]],
        _ => throw new ArgumentException("lookup_method must be mixed, transaction or receipt")
    };

    internal static int TargetCount(string value) => value is "256" or "4096" ? int.Parse(value, CultureInfo.InvariantCulture) : throw new ArgumentException("lookup_targets must be 256 or 4096");

    internal static string Patch(string script, bool requestIdentity = false)
    {
        if (!script.Contains(StrictCheck, StringComparison.Ordinal) || script.Contains(OriginalCheck, StringComparison.Ordinal))
        {
            if (script.Split(OriginalCheck, StringSplitOptions.None).Length != 2) throw new IOException("Unknown json-bench k6 result check; refusing to benchmark");
            script = script.Replace(OriginalCheck, StrictCheck, StringComparison.Ordinal);
        }
        if (requestIdentity)
        {
            const string import = "import exec from 'k6/execution';";
            const string anchor = "const payload = requestData[3];";
            const string identified = anchor + "\n    exec.vu.metrics.metadata.request_id = requestData[0];";
            if (script.Split(import, StringSplitOptions.None).Length != 2 || script.Split(anchor, StringSplitOptions.None).Length != 2 ||
                script.Replace(identified, "", StringComparison.Ordinal).Contains("request_id", StringComparison.Ordinal))
                throw new IOException("Unknown k6 request identity source; refusing indexed request IDs");
            if (!script.Contains(identified, StringComparison.Ordinal)) script = script.Replace(anchor, identified, StringComparison.Ordinal);
        }
        return script;
    }

    internal static async Task<JsonObject> WaitForHeadAsync(long expectedNumber, string? expectedHash, Func<string, JsonArray, Task<JsonNode?>> rpc, CancellationToken cancellationToken, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(120));
        string? previous = null;
        var observed = "<none>";
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var head = await rpc("eth_getBlockByNumber", new JsonArray("latest", false)).WaitAsync(deadline.Token) as JsonObject ?? throw new IOException("Missing snapshot head");
                var number = Quantity(head["number"]);
                var hash = head["hash"]?.GetValue<string>() ?? throw new IOException("Missing snapshot head hash");
                observed = $"{number}/{hash}";
                if (observed != previous) Console.WriteLine($"Snapshot restoration head {observed}; expected {expectedNumber}/{expectedHash ?? "stable hash"}");
                if (number > expectedNumber || number == expectedNumber && expectedHash is not null && hash != expectedHash) throw new IOException($"Snapshot restoration anchor mismatch: observed {observed}; expected {expectedNumber}/{expectedHash}");
                if (number == expectedNumber && observed == previous) return head;
                previous = observed;
                await Task.Delay(interval ?? TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException($"Snapshot restoration timed out: observed {observed}; expected {expectedNumber}/{expectedHash ?? "stable hash"}"); }
    }

    internal static async Task<JsonObject> PrepareAsync(string path, Func<string, JsonArray, Task<JsonNode?>> rpc, CancellationToken cancellationToken = default, JsonObject? stableHead = null, int targetCount = Count)
    {
        _ = TargetCount(targetCount.ToString(CultureInfo.InvariantCulture));
        if (File.Exists(path))
        {
            var existing = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? throw new IOException("Invalid lookup manifest");
            if (existing["entries"] is not JsonArray entriesArray || entriesArray.Count != targetCount) throw new IOException("Lookup manifest target count differs; refusing to regenerate");
            return existing;
        }
        var head = stableHead ?? await Block("latest");
        var number = Quantity(head["number"]);
        var entries = new JsonArray();
        var bytes = 0L;
        for (var scan = 0; scan < targetCount * 8 && entries.Count < targetCount && number >= 64 + scan; scan++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = await Block(Hex(number - 64 - scan));
            var transactions = block["transactions"] as JsonArray ?? throw new IOException("Missing block transactions");
            if (transactions.Count == 0) continue;
            var index = (entries.Count % 3) switch { 0 => 0, 1 => transactions.Count / 2, _ => transactions.Count - 1 };
            var entry = new JsonObject { ["txHash"] = transactions[index]!.DeepClone(), ["blockHash"] = block["hash"]!.DeepClone(), ["blockNumber"] = block["number"]!.DeepClone(), ["transactionIndex"] = Hex(index), ["blockTransactionCount"] = transactions.Count, ["blockSize"] = block["size"]?.DeepClone() };
            var responses = new JsonObject();
            foreach (var method in Methods) responses[method] = await Lookup(entry, method, rpc);
            entry["responses"] = responses;
            bytes += Encoding.UTF8.GetByteCount(entry.ToJsonString());
            if (bytes > 134217728) throw new IOException("Lookup golden results exceed 128 MiB budget");
            entries.Add((JsonNode)entry);
        }
        if (entries.Count != targetCount) throw new IOException($"Only {entries.Count}/{targetCount} nonempty snapshot blocks within bounded scan");
        var manifest = new JsonObject { ["version"] = Version, ["headNumber"] = head["number"]!.DeepClone(), ["headHash"] = head["hash"]!.DeepClone(), ["entries"] = entries };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path + ".tmp", manifest.ToJsonString(), cancellationToken);
        File.Move(path + ".tmp", path, overwrite: false);
        return manifest;

        async Task<JsonObject> Block(string value) => await rpc("eth_getBlockByNumber", new JsonArray(value, false)) as JsonObject ?? throw new IOException("Missing snapshot block");
    }

    internal static async Task ValidateAsync(JsonObject manifest, Func<string, JsonArray, Task<JsonNode?>> rpc, string report, string manifestHash, CancellationToken cancellationToken = default)
    {
        var result = new JsonObject { ["manifestSha256"] = manifestHash, ["passed"] = false, ["validatedResponses"] = 0 };
        try
        {
            if (manifest["version"]?.GetValue<string>() != Version || manifest["entries"] is not JsonArray entries || entries.Count is not (Count or 4096)) throw new IOException("Invalid lookup manifest version/count");
            if (await rpc("eth_getBlockByNumber", new JsonArray("latest", false)) is not JsonObject head || !JsonNode.DeepEquals(head["number"], manifest["headNumber"]) || !JsonNode.DeepEquals(head["hash"], manifest["headHash"])) throw new IOException("Snapshot head mismatch; refusing to change lookup targets");
            var blocks = new HashSet<string>(StringComparer.Ordinal);
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = node as JsonObject ?? throw new IOException("Invalid manifest entry");
                if (!blocks.Add(entry["blockNumber"]!.GetValue<string>()) || !hashes.Add(entry["txHash"]!.GetValue<string>())) throw new IOException("Duplicate lookup target");
                var block = await rpc("eth_getBlockByNumber", new JsonArray(entry["blockNumber"]!.DeepClone(), false)) as JsonObject;
                var ordinal = Quantity(entry["transactionIndex"]);
                if (block?["transactions"] is not JsonArray transactions || ordinal >= transactions.Count || !JsonNode.DeepEquals(block["hash"], entry["blockHash"]) || !JsonNode.DeepEquals(transactions[(int)ordinal], entry["txHash"])) throw new IOException("Canonical lookup target changed");
                foreach (var method in Methods)
                {
                    var response = await Lookup(entry, method, rpc);
                    if (!JsonNode.DeepEquals(response, entry["responses"]?[method])) throw new IOException($"Lookup response differs from baseline: {method} {entry["txHash"]}");
                    result["validatedResponses"] = result["validatedResponses"]!.GetValue<int>() + 1;
                }
            }
            result["passed"] = true;
        }
        catch (Exception exception) { result["error"] = exception.Message; throw; }
        finally { await File.WriteAllTextAsync(report, result.ToJsonString()); }
    }

    private static async Task<JsonObject> Lookup(JsonObject entry, string method, Func<string, JsonArray, Task<JsonNode?>> rpc)
    {
        var response = await rpc(method, new JsonArray(entry["txHash"]!.DeepClone())) as JsonObject ?? throw new IOException($"Null/nonobject {method} result");
        foreach (var (actual, expected) in new[] { (method == Methods[0] ? "hash" : "transactionHash", "txHash"), ("blockHash", "blockHash"), ("blockNumber", "blockNumber"), ("transactionIndex", "transactionIndex") })
            if (entry[expected] is null || !JsonNode.DeepEquals(response[actual], entry[expected])) throw new IOException($"Mismatched {method} {actual}");
        return response;
    }

    internal static async Task<JsonNode?> RpcAsync(HttpClient http, string url, string method, JsonArray parameters, CancellationToken cancellationToken)
    {
        using var content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject ?? throw new IOException("Invalid RPC response");
        if (body.ContainsKey("error")) throw new IOException($"RPC error: {body["error"]}");
        return body["result"]?.DeepClone();
    }

    internal static void Fixtures(JsonObject manifest, string source, YamlMappingNode config, string preset = "mixed")
    {
        var directory = Path.Combine(source, "rpc-calls", Name); Directory.CreateDirectory(directory);
        var calls = new YamlSequenceNode();
        foreach (var method in SelectedMethods(preset))
        {
            var rows = manifest["entries"]!.AsArray().Select(entry => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = new JsonArray(entry!["txHash"]!.DeepClone()) }.ToJsonString());
            File.WriteAllLines(Path.Combine(directory, method + ".jsonl"), rows);
            calls.Add(new YamlMappingNode { { "name", method + " snapshot" }, { "file", "./rpc-calls/" + Name + "/" + method + ".jsonl" }, { "file_type", "jsonl" }, { "weight", "1" }, { "thresholds", new YamlSequenceNode { "p(99)<600000" } } });
        }
        config.Children[new YamlScalarNode("calls")] = calls;
        config.Children[new YamlScalarNode("test_name")] = new YamlScalarNode(Name);
    }

    internal static void CheckSampleBudget(string rate, string duration, string vus)
    {
        if (!int.TryParse(rate, NumberStyles.None, CultureInfo.InvariantCulture, out var rps) || rps is < 1 or > 500 ||
            !duration.EndsWith('s') || !int.TryParse(duration[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 120 ||
            !int.TryParse(vus, NumberStyles.None, CultureInfo.InvariantCulture, out var workers) || workers is < 1 or > 256)
            throw new IOException("Lookup samples require explicit 1..500 rps, 1..120 seconds and 1..256 VUs");
    }

    internal static void CheckSamples(string path, JsonObject summary, string preset = "mixed")
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 268435456) throw new IOException("Missing/oversized lookup samples (256 MiB limit)");
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var counts = SelectedMethods(preset).ToDictionary(method => method, _ => 0L, StringComparer.Ordinal);
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++lines > 2000000 || line.Length > 65536) throw new IOException("Lookup samples exceed bounded record budget");
            var sample = JsonNode.Parse(line) as JsonObject ?? throw new IOException("Invalid k6 sample");
            if (sample["type"]?.GetValue<string>() != "Point" || sample["metric"]?.GetValue<string>() != "http_req_duration") continue;
            var data = sample["data"]!;
            var method = data["tags"]?["rpc_method"]?.GetValue<string>() ?? "";
            if (!counts.ContainsKey(method) || !DateTimeOffset.TryParse(data["time"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                data["value"] is not JsonValue value || !value.TryGetValue<double>(out var latency) || !double.IsFinite(latency) || latency < 0 ||
                data["tags"] is JsonObject tags && tags.ContainsKey("request_id") ||
                data["metadata"]?["request_id"]?.GetValue<string>() is not { Length: > 0 } requestId || !requestIds.Add(requestId) || requestIds.Count > 66000) throw new IOException("Invalid/duplicate lookup latency point");
            counts[method]++;
        }
        double MetricCount(string key) => double.TryParse((summary["metrics"]?[key]?["values"]?["count"] ?? summary["metrics"]?[key]?["count"])?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && double.IsFinite(count) ? count : throw new IOException("Missing sample count metric");
        if (counts.Values.Sum() != MetricCount("http_reqs")) throw new IOException("Lookup latency sample count differs from delivered requests");
        foreach (var (method, count) in counts)
        {
            var groups = summary["metrics"]!.AsObject().Select(pair => pair.Key).Where(key => key.StartsWith("http_reqs{", StringComparison.Ordinal) && key.Contains("scenario:", StringComparison.Ordinal) && key.Split("req_name:", StringSplitOptions.None) is [_, var tags] && tags.Split(',', '}')[0].Trim().Trim('\'', '"') == method + " snapshot");
            if (count <= 0 || count != groups.Sum(MetricCount)) throw new IOException("Lookup method sample count differs from delivered requests");
        }
    }

    internal static async Task QualifyReceiptEncodingAsync(JsonObject manifest, Func<string, JsonArray, Task<JsonNode?>> rpc, string reportPath, CancellationToken cancellationToken)
    {
        var records = new JsonArray();
        foreach (var entry in manifest["entries"]!.AsArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = Quantity(entry!["blockNumber"]);
            var hash = entry["blockHash"]!.GetValue<string>();
            var key = "0x" + number.ToString("x16", CultureInfo.InvariantCulture) + hash[2..];
            var value = await rpc("debug_getFromDb", new JsonArray("ReceiptsBlocks", key));
            var layout = "number-prefixed";
            if (value is null || value.GetValue<string>() == "0x")
            {
                value = await rpc("debug_getFromDb", new JsonArray("ReceiptsBlocks", hash));
                layout = "legacy-hash";
            }
            var hex = value?.GetValue<string>();
            if (hex is not null && (!hex.StartsWith("0x", StringComparison.Ordinal) || hex.Length % 2 != 0)) throw new IOException("Invalid raw receipt encoding probe");
            var bytes = hex is null ? Array.Empty<byte>() : Convert.FromHexString(hex[2..]);
            var encoding = bytes.Length == 0 ? "missing-or-empty" : bytes[0] switch
            {
                127 => "compact-no-hashes",
                126 when bytes.Length > 1 && bytes[1] == 128 => "compact-hash-prefix-empty",
                126 => "compact-hash-prefixed",
                _ => "noncompact-or-unknown"
            };
            records.Add((JsonNode)new JsonObject { ["blockHash"] = hash, ["blockNumber"] = entry["blockNumber"]!.DeepClone(), ["keyLayout"] = layout,
                ["encoding"] = encoding, ["byteLength"] = bytes.Length, ["prefixHex"] = Convert.ToHexStringLower(bytes.AsSpan(0, Math.Min(96, bytes.Length))),
                ["valueSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }
        await File.WriteAllTextAsync(reportPath, new JsonObject { ["scope"] = "Stored receipt marker inspection; not proof of an executed recovery path",
            ["headNumber"] = manifest["headNumber"]!.DeepClone(), ["headHash"] = manifest["headHash"]!.DeepClone(), ["records"] = records }.ToJsonString(), cancellationToken);
    }

    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static long Quantity(JsonNode? value) => Convert.ToInt64(value?.GetValue<string>() ?? throw new IOException("Missing quantity"), 16);
    private static string Hex(long value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    internal static void CheckSummary(JsonObject summary, string preset = "mixed")
    {
        var metrics = summary["metrics"] as JsonObject ?? throw new IOException("Missing k6 metrics");
        double Number(string metric, string field)
        {
            var node = metrics[metric];
            var fieldValue = node?["values"]?[field] ?? node?[field];
            if (field == "rate" && fieldValue is null) fieldValue = node?["values"]?["value"] ?? node?["value"];
            var value = fieldValue?.ToJsonString();
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : throw new IOException($"Missing k6 {metric}.{field}");
        }
        var count = Number("http_reqs", "count");
        if (count <= 0 || Number("http_req_failed", "rate") != 0 || Number("checks", "fails") != 0 || Number("checks", "passes") != count * 2) throw new IOException("Lookup k6 requests/checks failed or incomplete");
        double methodCount = 0;
        foreach (var method in SelectedMethods(preset))
        {
            var groups = metrics.Select(pair => pair.Key).Where(key => key.StartsWith("http_reqs{", StringComparison.Ordinal) && key.Contains("scenario:", StringComparison.Ordinal) && key.Split("req_name:", StringSplitOptions.None) is [_, var tags] && tags.Split(',', '}')[0].Trim().Trim('\'', '"') == method + " snapshot").ToArray();
            if (groups.Length == 0) throw new IOException($"Missing lookup method metric group: {method}");
            foreach (var group in groups)
            {
                var delivered = Number(group, "count");
                var tags = group["http_reqs".Length..];
                if (delivered <= 0 || Number("http_req_failed" + tags, "rate") != 0 || Number("http_req_duration" + tags, "avg") <= 0) throw new IOException($"Empty/failed lookup method metric group: {method}");
                methodCount += delivered;
            }
        }
        if (methodCount != count) throw new IOException("Lookup per-method request counts do not match overall requests");
    }
}
