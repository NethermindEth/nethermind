// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal sealed class CorpusParityError(string message) : Exception(message);

internal sealed partial class CorpusParity : IDisposable
{
    internal static readonly string[] CounterFields = ["total", "matched", "both_rpc_errors", "baseline_rpc_errors", "candidate_rpc_errors", "candidate_transport_failures", "candidate_invalid_responses", "baseline_shorter", "candidate_shorter", "length_mismatches", "content_mismatches"];
    internal static readonly string[] LabelFields = ["baseline_client", "candidate_client"];
    internal const string ErrorMarker = "!rpc_error";
    internal const int MaxDiffWords = 8;
    private readonly Func<string, string?> _environment;
    private readonly HttpClient _client;
    internal readonly int MaxCorpusRecords;
    internal readonly int MaxResponseBytes;
    internal readonly int MaxDivergenceIndexes;
    internal readonly int ReplayConcurrency;

    internal CorpusParity(Func<string, string?> environment, HttpMessageHandler? handler = null)
    {
        _environment = environment;
        MaxCorpusRecords = EnvInt(environment("RPC_BENCH_MAX_CORPUS_RECORDS"), 10000);
        MaxResponseBytes = EnvInt(environment("RPC_BENCH_MAX_RESPONSE_BYTES"), 16 * 1024 * 1024);
        MaxDivergenceIndexes = EnvInt(environment("RPC_BENCH_MAX_DIVERGENCE_INDEXES"), 200);
        ReplayConcurrency = EnvInt(environment("RPC_BENCH_PARITY_CONCURRENCY"), 16);
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public void Dispose() => _client.Dispose();
    internal static int EnvInt(string? text, int fallback) => int.TryParse(text, out var value) && value > 0 ? value : fallback;
    internal static string Text(JsonNode? value) => value?.ToString() ?? "";
    internal static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(value => (JsonNode?)JsonValue.Create(value))];
    internal static void Save(string path, JsonNode document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, document.ToJsonString() + "\n");
    }

    internal (string Method, JsonObject Options) Rewrite()
    {
        var method = _environment("RPC_BENCH_CORPUS_METHOD");
        if (string.IsNullOrEmpty(method)) method = "eth_call";
        if (method == "eth_call") return (method, []);
        if (method == "debug_traceCall")
        {
            var tracer = _environment("RPC_BENCH_CORPUS_TRACER") ?? "callTracer";
            if (tracer is not ("callTracer" or "prestateTracer" or "4byteTracer" or "stateGasTracer" or ""))
                throw new CorpusParityError("unknown tracer (expected one of: 4byteTracer, callTracer, prestateTracer, stateGasTracer, or empty for struct logs)");
            return (method, new JsonObject { ["tracer"] = tracer });
        }
        if (method != "trace_call") throw new CorpusParityError("unknown corpus method (expected one of: eth_call, debug_traceCall, trace_call)");
        var types = (_environment("RPC_BENCH_CORPUS_TRACE_TYPES") ?? "").Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (types.Length == 0) types = ["trace"];
        if (types.Any(type => type.ToLowerInvariant() is not ("vmtrace" or "statediff" or "trace" or "rewards" or "all")))
            throw new CorpusParityError("unknown trace type (expected any of: all, rewards, statediff, trace, vmtrace)");
        return (method, new JsonObject { ["trace_types"] = Strings(types) });
    }

    internal static JsonArray RewriteRecord(JsonArray parameters, string method, JsonObject options)
    {
        if (method == "eth_call") return parameters.DeepClone().AsArray();
        if (parameters.Count == 0) throw new CorpusParityError($"record has no params — cannot rewrite as {method}");
        var block = parameters.Count > 1 && parameters[1] is not null ? parameters[1]!.DeepClone() : JsonValue.Create("latest");
        if (method == "debug_traceCall")
        {
            var settings = new JsonObject();
            if (Text(options["tracer"]).Length > 0) settings["tracer"] = options["tracer"]!.DeepClone();
            if (parameters.Count > 2 && parameters[2] is not null) settings["stateOverrides"] = parameters[2]!.DeepClone();
            if (parameters.Count > 3 && parameters[3] is not null) settings["blockOverrides"] = parameters[3]!.DeepClone();
            return [parameters[0]?.DeepClone(), block, settings];
        }
        if (parameters.Count > 3 && parameters[3] is not null) throw new CorpusParityError("record has blockOverrides, which trace_call cannot express");
        var rewritten = new JsonArray(parameters[0]?.DeepClone(), options["trace_types"]!.DeepClone(), block);
        if (parameters.Count > 2 && parameters[2] is not null) rewritten.Add(parameters[2]!.DeepClone());
        return rewritten;
    }

    internal static StreamReader OpenCorpus(string path)
    {
        if (!path.EndsWith(".jsonl", StringComparison.Ordinal) && !path.EndsWith(".jsonl.gz", StringComparison.Ordinal))
            throw new CorpusParityError("corpus must have a .jsonl or .jsonl.gz extension");
        var file = File.OpenRead(path);
        try { return new StreamReader(path.EndsWith(".gz", StringComparison.Ordinal) ? new CorpusGzipStream(file) : file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false); }
        catch { file.Dispose(); throw; }
    }

    internal IEnumerable<JsonArray> ReadCorpus(string path)
    {
        var (method, options) = Rewrite();
        StreamReader reader;
        try { reader = OpenCorpus(path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { throw new CorpusParityError($"cannot read corpus: {exception.GetType().Name}"); }
        using (reader)
        {
            var records = 0;
            for (var number = 1; ; number++)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch (Exception exception) when (exception is IOException or DecoderFallbackException or InvalidDataException) { throw new CorpusParityError($"cannot read corpus: {exception.GetType().Name}"); }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (records >= MaxCorpusRecords) throw new CorpusParityError($"corpus exceeds {MaxCorpusRecords} records");
                JsonNode? record;
                try { record = JsonNode.Parse(line); }
                catch (JsonException) { throw new CorpusParityError($"corpus line {number}: invalid JSON"); }
                if (record is not JsonObject obj || Text(obj["method"]) != "eth_call" || obj["params"] is not JsonArray parameters)
                    throw new CorpusParityError($"corpus line {number}: not an eth_call record");
                records++;
                JsonArray result;
                try { result = RewriteRecord(parameters, method, options); }
                catch (CorpusParityError error) { throw new CorpusParityError($"corpus line {number}: {error.Message}"); }
                yield return result;
            }
        }
    }

    internal JsonArray[] LoadCorpus(string path)
    {
        var start = Stopwatch.GetTimestamp();
        var records = ReadCorpus(path).ToArray();
        if (records.Length == 0) throw new CorpusParityError("corpus contains no records");
        var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (seconds > 5) Console.WriteLine($"  loaded {records.Length} records in {seconds:F0}s");
        return records;
    }

    private static JsonObject Request(string method, int index, JsonArray parameters) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = index, ["method"] = method, ["params"] = parameters.DeepClone()
    };

    private async Task<(int Status, byte[] Body)?> Fetch(string url, JsonObject body, int limit, bool retry, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < (retry ? 2 : 1); attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[(int)Math.Min(8192L, limit + 1L)];
                while (buffer.Length <= limit)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1L - buffer.Length)), timeout.Token);
                    if (read == 0) break;
                    buffer.Write(chunk, 0, read);
                }
                return ((int)response.StatusCode, buffer.ToArray());
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UriFormatException or ArgumentException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested) { if (!retry || attempt == 1) return null; }
        }
        return null;
    }

    internal async Task<(long Head, long Chain, string Hash)> NodeIdentity(string url, CancellationToken cancellationToken)
    {
        async Task<JsonNode?> Rpc(string method, JsonArray parameters)
        {
            var fetched = await Fetch(url, Request(method, 1, parameters), 1 << 20, false, cancellationToken);
            try
            {
                if (fetched is null || fetched.Value.Status >= 400) throw new JsonException();
                var node = JsonNode.Parse(fetched.Value.Body);
                if (node is not JsonObject obj || !obj.ContainsKey("result")) throw new JsonException();
                return obj["result"];
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { throw new CorpusParityError($"cannot read {method} from the node"); }
        }
        var chain = await Rpc("eth_chainId", []);
        var header = await Rpc("eth_getBlockByNumber", new JsonArray("latest", false));
        try
        {
            return (Convert.ToInt64(Text(header!["number"]), 16), Convert.ToInt64(Text(chain), 16), Text(header["hash"]).ToLowerInvariant());
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidOperationException or NullReferenceException)
        { throw new CorpusParityError("node returned an unusable head header"); }
    }

    internal async Task<(string? Category, string Result)> Post(string url, int index, JsonArray parameters, string method, CancellationToken cancellationToken)
    {
        var fetched = await Fetch(url, Request(method, index, parameters), MaxResponseBytes, true, cancellationToken);
        if (fetched is null) return ("transport_failure", "");
        var (status, raw) = fetched.Value;
        if (raw.Length > MaxResponseBytes) return ("invalid_response:response_too_large", "");
        JsonObject? envelope;
        try { envelope = JsonNode.Parse(raw, documentOptions: RpcJson.DocumentOptions) as JsonObject; }
        catch (JsonException) { return (status >= 400 ? "transport_failure" : "invalid_response", ""); }
        var sameId = envelope?["id"] is JsonValue id && id.TryGetValue<int>(out var resultId) && resultId == index;
        if (status >= 400)
        {
            if (envelope is not null && envelope.ContainsKey("error") && (sameId || envelope["id"] is null)) return (RpcError(envelope["error"]), "");
            return ("transport_failure", "");
        }
        if (envelope is null || !sameId) return ("invalid_response", "");
        if (envelope.ContainsKey("error")) return (RpcError(envelope["error"]), "");
        var result = envelope["result"];
        if (method != "eth_call")
        {
            if (result is null) return ("invalid_response", "");
            try
            {
                using var document = JsonDocument.Parse(result.ToJsonString(RpcJson.SerializerOptions), RpcJson.DocumentOptions);
                return (null, "0x" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RpcJson.Canonical(document.RootElement, compact: true)))));
            }
            catch (JsonException) { return ("invalid_response", ""); }
        }
        if (result is not JsonValue value || !value.TryGetValue<string>(out var text) || !HexBytesRegex().IsMatch(text)) return ("invalid_response", "");
        return (null, text.ToLowerInvariant());
    }

    private static string RpcError(JsonNode? error) => error is JsonObject obj && obj["code"] is JsonValue code && code.TryGetValue<long>(out var number) ? $"rpc_error:{number}" : "rpc_error";
    internal static string? BaseCategory(string? category) => category?.Split(':', 2)[0];

    internal async Task Probe(string corpus, string url, CancellationToken cancellationToken)
    {
        var method = Rewrite().Method;
        var records = method == "eth_call" ? LoadCorpus(corpus).Take(1) : ReadCorpus(corpus);
        var count = 0;
        foreach (var parameters in records)
        {
            var (category, _) = await Post(url, ++count, parameters, method, cancellationToken);
            if (category is null) { Console.WriteLine($"corpus method probe OK: {method}"); return; }
            if (category == "invalid_response:response_too_large") throw new CorpusParityError($"corpus method probe failed: response_too_large (MAX_RESPONSE_BYTES={MaxResponseBytes}); increase tool_config.max_response_bytes / RPC_BENCH_MAX_RESPONSE_BYTES");
            if (category.StartsWith("rpc_error:", StringComparison.Ordinal) && long.TryParse(category[10..], out var code) && code is not (-32600 or -32601))
            {
                if (method != "eth_call") continue;
                Console.WriteLine($"corpus method probe accepted RPC error: {method}"); return;
            }
            throw new CorpusParityError($"corpus method probe failed: {category}");
        }
        throw new CorpusParityError(count == 0 ? "corpus contains no records" : $"corpus method probe found no successful results over {count} records");
    }

    internal async Task<(string? Category, string Result)[]> Replay(string url, JsonArray[] records, string what, string method, CancellationToken cancellationToken)
    {
        var results = new (string? Category, string Result)[records.Length];
        var started = Stopwatch.GetTimestamp();
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, records.Length), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(ReplayConcurrency, records.Length)), CancellationToken = cancellationToken }, async (index, token) =>
        {
            results[index] = await Post(url, index + 1, records[index], method, token);
            Progress(Interlocked.Increment(ref done), records.Length, started, what);
        });
        var suspect = Enumerable.Range(0, results.Length).Where(index => BaseCategory(results[index].Category) is "transport_failure" or "invalid_response").ToArray();
        if (suspect.Length > 0)
        {
            Console.WriteLine($"  {what}: re-running {suspect.Length} non-clean record(s) serially");
            var recovered = 0;
            foreach (var index in suspect)
            {
                results[index] = await Post(url, index + 1, records[index], method, cancellationToken);
                if (BaseCategory(results[index].Category) is not ("transport_failure" or "invalid_response")) recovered++;
            }
            if (recovered > 0) Console.WriteLine($"  {what}: {recovered} recovered on retry (concurrency artifacts, not defects)");
        }
        return results;
    }

    internal async Task Baseline(string corpus, string url, string statePath, CancellationToken cancellationToken)
    {
        var records = LoadCorpus(corpus);
        var (head, chain, hash) = await NodeIdentity(url, cancellationToken);
        var method = Rewrite().Method;
        var results = new List<string>();
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var errors = 0;
        foreach (var (category, result) in await Replay(url, records, "baseline", method, cancellationToken))
        {
            if (BaseCategory(category) == "rpc_error") { errors++; results.Add(ErrorMarker); continue; }
            if (category is not null) failures[category] = failures.GetValueOrDefault(category) + 1;
            results.Add(result);
        }
        if (failures.Count > 0) throw new CorpusParityError($"baseline replay had failures over {records.Length} records: {string.Join(' ', failures.Select(pair => $"{pair.Key}={pair.Value}"))}");
        if (method != "eth_call" && errors == results.Count) throw new CorpusParityError($"trace baseline produced no successful results over {results.Count} records");
        var document = new JsonObject { ["total"] = results.Count, ["head"] = head, ["chain_id"] = chain, ["block_hash"] = hash, ["results"] = Strings(results) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        using (var file = File.Create(statePath))
        using (var compressed = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new StreamWriter(compressed)) await writer.WriteAsync(document.ToJsonString());
        Console.WriteLine($"baseline captured: {results.Count} outcomes ({errors} rpc_error) at head {head}");
        if (errors > 0) Console.WriteLine($"baseline rpc_error indexes (first {Math.Min(errors, 40)}): {string.Join(' ', results.Select((result, index) => (result, index)).Where(item => item.result == ErrorMarker).Take(40).Select(item => item.index + 1))}");
    }

    internal static JsonObject DescribeDivergence(int index, string expected, string actual)
    {
        var exp = expected.StartsWith("0x", StringComparison.Ordinal) ? expected[2..] : expected;
        var act = actual.StartsWith("0x", StringComparison.Ordinal) ? actual[2..] : actual;
        var words = new JsonArray();
        var differing = 0;
        for (var word = 0; word <= Math.Max(exp.Length, act.Length) / 64; word++)
        {
            var start = word * 64;
            var a = start >= exp.Length ? "" : exp.Substring(start, Math.Min(64, exp.Length - start));
            var b = start >= act.Length ? "" : act.Substring(start, Math.Min(64, act.Length - start));
            if (a == b) continue;
            differing++;
            if (words.Count >= MaxDiffWords) continue;
            var item = new JsonObject { ["word"] = word };
            if (a.Length > 0 && b.Length > 0 && BigInteger.TryParse("0" + a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) && BigInteger.TryParse("0" + b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var second))
                item["direction"] = second > first ? "higher" : "lower";
            words.Add((JsonNode)item);
        }
        return new JsonObject
        {
            ["index"] = index, ["baseline_bytes"] = exp.Length / 2, ["candidate_bytes"] = act.Length / 2,
            ["baseline_all_zero"] = exp.Length > 0 && exp.All(character => character == '0'), ["candidate_all_zero"] = act.Length > 0 && act.All(character => character == '0'),
            ["differing_words"] = words, ["total_differing_words"] = differing
        };
    }

    internal async Task<bool> Compare(string corpus, string url, string statePath, string reportPath, string baselineClient, string candidateClient, string? diffsPath, CancellationToken cancellationToken)
    {
        var method = Rewrite().Method;
        if (!string.IsNullOrEmpty(diffsPath) && method != "eth_call") throw new CorpusParityError("--diffs cannot characterise trace responses (outcomes are digests)");
        var records = LoadCorpus(corpus);
        JsonObject state;
        string[] baseline;
        try
        {
            using var file = File.OpenRead(statePath);
            using var compressed = new GZipStream(file, CompressionMode.Decompress);
            state = JsonNode.Parse(compressed)!.AsObject();
            baseline = state["results"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or NullReferenceException or UnauthorizedAccessException)
        { throw new CorpusParityError("baseline state is missing or unreadable"); }
        if (baseline.Length != records.Length) throw new CorpusParityError($"baseline state has {baseline.Length} results but the corpus has {records.Length}");
        var (head, chain, hash) = await NodeIdentity(url, cancellationToken);
        if (Text(state["head"]) != head.ToString(CultureInfo.InvariantCulture) || Text(state["chain_id"]) != chain.ToString(CultureInfo.InvariantCulture) || state["block_hash"] is not null && Text(state["block_hash"]) != hash)
            throw new CorpusParityError($"node identity mismatch: baseline head={state["head"]} chain={state["chain_id"]} hash={state["block_hash"]} vs candidate head={head} chain={chain} hash={hash} — align the snapshots before comparing");
        var report = CounterFields.ToDictionary(field => field, _ => 0);
        report["total"] = records.Length;
        var divergences = new JsonArray();
        var diffs = new JsonArray();
        var replayed = await Replay(url, records, "compare", method, cancellationToken);
        var disputed = Enumerable.Range(0, replayed.Length).Where(index => !(replayed[index].Category is null && replayed[index].Result == baseline[index]) && !(BaseCategory(replayed[index].Category) == "rpc_error" && baseline[index] == ErrorMarker)).ToArray();
        if (disputed.Length > 0)
        {
            Console.WriteLine($"  compare: re-verifying {disputed.Length} disagreement(s) serially");
            var settled = 0;
            foreach (var index in disputed)
            {
                var retried = await Post(url, index + 1, records[index], method, cancellationToken);
                if (retried != replayed[index]) settled++;
                replayed[index] = retried;
            }
            if (settled > 0) Console.WriteLine($"  compare: {settled} disagreement(s) changed outcome on retry");
        }
        for (var index = 0; index < replayed.Length; index++)
        {
            var (category, actual) = replayed[index];
            var expected = baseline[index];
            var field = BaseCategory(category) switch
            {
                "transport_failure" => "candidate_transport_failures",
                "invalid_response" => "candidate_invalid_responses",
                "rpc_error" => expected == ErrorMarker ? "both_rpc_errors" : "candidate_rpc_errors",
                _ => expected == ErrorMarker ? "baseline_rpc_errors" : actual == expected ? "matched" : actual.Length == expected.Length ? "content_mismatches" :
                    expected.Length < actual.Length && actual.StartsWith(expected, StringComparison.Ordinal) ? "baseline_shorter" :
                    actual.Length < expected.Length && expected.StartsWith(actual, StringComparison.Ordinal) ? "candidate_shorter" : "length_mismatches"
            };
            report[field]++;
            if (field is not ("matched" or "both_rpc_errors") && divergences.Count < MaxDivergenceIndexes)
            {
                var kind = field switch { "candidate_transport_failures" => "candidate_transport_failure", "candidate_invalid_responses" => "candidate_invalid_response", "candidate_rpc_errors" => "candidate_rpc_error", "baseline_rpc_errors" => "baseline_rpc_error", "length_mismatches" => "length_mismatch", "content_mismatches" => "content_mismatch", _ => field };
                divergences.Add((JsonNode)new JsonObject { ["index"] = index + 1, ["kind"] = kind });
            }
            if (field == "content_mismatches" && !string.IsNullOrEmpty(diffsPath)) diffs.Add((JsonNode)DescribeDivergence(index + 1, expected, actual));
        }
        if (!string.IsNullOrEmpty(diffsPath) && diffs.Count > 0)
        {
            Save(diffsPath, new JsonObject { ["baseline_client"] = baselineClient, ["candidate_client"] = candidateClient, ["total_divergences"] = report["content_mismatches"], ["recorded"] = diffs.Count, ["diffs"] = diffs });
            Console.WriteLine($"  wrote {diffs.Count} divergence characterisations to {Path.GetFileName(diffsPath)}");
        }
        var document = new JsonObject { ["baseline_client"] = baselineClient, ["candidate_client"] = candidateClient, ["divergences"] = divergences };
        foreach (var (field, value) in report) document[field] = value;
        Save(reportPath, document);
        var clean = report["matched"] + report["both_rpc_errors"] == report["total"] && (method == "eth_call" || report["matched"] > 0);
        var defects = string.Join(' ', report.Where(pair => pair.Value > 0 && pair.Key is not ("total" or "matched" or "both_rpc_errors")).Select(pair => $"{pair.Key}={pair.Value}"));
        var agreement = $"{report["matched"]}/{report["total"]} matched" + (report["both_rpc_errors"] > 0 ? $" + {report["both_rpc_errors"]} both-error" : "");
        Console.WriteLine($"parity {candidateClient} vs {baselineClient}: {agreement}" + (defects.Length > 0 ? $" ({defects})" : ""));
        if (divergences.Count > 0) Console.WriteLine($"divergent corpus indexes (first {Math.Min(divergences.Count, 40)}): {string.Join(' ', divergences.Take(40).Select(value => value!["index"]))}");
        return clean;
    }

    private static void Progress(int done, int total, long started, string what)
    {
        if (done % 2000 != 0 && done != total) return;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var rate = elapsed > 0 ? done / elapsed : 0;
        Console.WriteLine($"  {what} {done}/{total} ({done * 100d / total:F0}%) {rate:F0} req/s, ~{(rate > 0 ? (total - done) / rate / 60 : 0):F1} min left");
    }

    internal async Task Timings(string corpus, string url, string output, int passes, double rps, int concurrency, int warmupSeconds, double warmupRps, CancellationToken cancellationToken)
    {
        var records = LoadCorpus(corpus);
        var method = Rewrite().Method;
        if (passes < 1) throw new CorpusParityError("passes must be >= 1");
        if (concurrency < 1) throw new CorpusParityError("concurrency must be >= 1");
        var (head, chain, hash) = await NodeIdentity(url, cancellationToken);
        var grid = new double[records.Length, passes];
        var status = new string[records.Length, passes];
        var outcomes = new Dictionary<string, int>();
        var started = Stopwatch.GetTimestamp();
        var issued = checked(passes * records.Length);
        await Parallel.ForEachAsync(Enumerable.Range(0, issued), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken }, async (order, token) =>
        {
            var record = order % records.Length;
            var pass = order / records.Length;
            var delay = rps > 0 ? order / rps - Stopwatch.GetElapsedTime(started).TotalSeconds : 0;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token);
            var requestStart = Stopwatch.GetTimestamp();
            string? category;
            try { (category, _) = await Post(url, record + 1, records[record], method, token); }
            catch (Exception exception) when (exception is not OperationCanceledException) { category = "transport_failure"; }
            var outcome = category ?? "ok";
            grid[record, pass] = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
            status[record, pass] = outcome;
            lock (outcomes) outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        });
        var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var writer = new StreamWriter(output))
        {
            writer.NewLine = "\r\n";
            writer.WriteLine(string.Join(',', new[] { "record_index" }.Concat(Enumerable.Range(1, passes).SelectMany(pass => new[] { $"pass_{pass}_ms", $"pass_{pass}_status" }))));
            for (var record = 0; record < records.Length; record++)
                writer.WriteLine(string.Join(',', new[] { (record + 1).ToString(CultureInfo.InvariantCulture) }.Concat(Enumerable.Range(0, passes).SelectMany(pass => new[] { grid[record, pass].ToString("F3", CultureInfo.InvariantCulture), status[record, pass] }))));
        }
        var achieved = wall > 0 ? issued / wall : 0;
        var counts = new JsonObject();
        foreach (var (key, count) in outcomes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) counts[key] = count;
        Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "timings.meta.json"), new JsonObject
        {
            ["head"] = head, ["chain_id"] = chain, ["block_hash"] = hash, ["records"] = records.Length, ["passes"] = passes, ["requests"] = issued,
            ["target_rps"] = rps, ["achieved_rps"] = Math.Round(achieved, 2), ["concurrency"] = concurrency,
            ["warmup_seconds"] = warmupSeconds, ["warmup_rps"] = warmupRps, ["outcomes"] = counts
        });
        Console.WriteLine($"timings: {records.Length} records x {passes} passes = {issued} requests at head {head} chain {chain}");
        Console.WriteLine($"  wall {wall:F1}s, achieved {achieved:F1} rps" + (rps > 0 ? $" (target {rps})" : " (unpaced)"));
        Console.WriteLine($"  outcomes: {string.Join(", ", outcomes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}");
        var failed = outcomes.Where(pair => pair.Key != "ok").Sum(pair => pair.Value);
        if (failed > 0) Console.WriteLine($"  WARNING: {failed}/{issued} ({failed * 100d / issued:F1}%) did not return a result — latency percentiles over this matrix are NOT comparable to a clean run, because failures return early and pull every percentile down");
    }

    internal async Task<int> Main(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length == 0 || args[0] is not ("validate" or "probe" or "baseline" or "compare" or "timings")) throw new CorpusParityError("expected validate, probe, baseline, compare or timings");
            string[] required = args[0] switch
            {
                "validate" => ["--corpus"],
                "probe" => ["--corpus", "--rpc-url"],
                "baseline" => ["--corpus", "--rpc-url", "--state"],
                "compare" => ["--corpus", "--rpc-url", "--state", "--report", "--baseline-client", "--candidate-client"],
                _ => ["--corpus", "--rpc-url", "--out"]
            };
            var allowed = required.Concat(args[0] switch
            {
                "compare" => new[] { "--diffs" },
                "timings" => ["--passes", "--rps", "--concurrency", "--warmup-seconds", "--warmup-rps"],
                _ => []
            }).ToHashSet(StringComparer.Ordinal);
            var values = new Dictionary<string, string>();
            for (var index = 1; index < args.Length; index += 2)
            {
                if (!allowed.Contains(args[index]) || index + 1 >= args.Length) throw new CorpusParityError("expected named option and value");
                values[args[index]] = args[index + 1];
            }
            string Required(string name) => values.GetValueOrDefault(name) ?? throw new CorpusParityError($"missing required option {name}");
            foreach (var name in required) Required(name);
            var corpus = Required("--corpus");
            var (method, options) = Rewrite();
            if (args[0] == "validate")
            {
                var detail = method switch
                {
                    "debug_traceCall" => $" (replayed as {method}, tracer={(Text(options["tracer"]).Length == 0 ? "structLog" : Text(options["tracer"]))})",
                    "trace_call" => $" (replayed as {method}, types={string.Join('+', options["trace_types"]!.AsArray().Select(Text))})",
                    _ => ""
                };
                Console.WriteLine($"corpus OK: {LoadCorpus(corpus).Length} records{detail}"); return 0;
            }
            var url = Required("--rpc-url");
            if (args[0] == "probe" || method != "eth_call") await Probe(corpus, url, cancellationToken);
            if (args[0] == "probe") return 0;
            if (args[0] == "baseline") { await Baseline(corpus, url, Required("--state"), cancellationToken); return 0; }
            if (args[0] == "compare") return await Compare(corpus, url, Required("--state"), Required("--report"), Required("--baseline-client"), Required("--candidate-client"), values.GetValueOrDefault("--diffs"), cancellationToken) ? 0 : 1;
            await Timings(corpus, url, Required("--out"), int.Parse(values.GetValueOrDefault("--passes", "1"), CultureInfo.InvariantCulture),
                double.Parse(values.GetValueOrDefault("--rps", "0"), CultureInfo.InvariantCulture), int.Parse(values.GetValueOrDefault("--concurrency", "16"), CultureInfo.InvariantCulture),
                int.Parse(values.GetValueOrDefault("--warmup-seconds", "0"), CultureInfo.InvariantCulture), double.Parse(values.GetValueOrDefault("--warmup-rps", "0"), CultureInfo.InvariantCulture), cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Console.Error.WriteLine("error: replay cancelled"); return 130; }
        catch (CorpusParityError error) { Console.Error.WriteLine($"error: {error.Message}"); return 2; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or OverflowException)
        { Console.Error.WriteLine($"error: operation failed ({exception.GetType().Name})"); return 2; }
    }
    [GeneratedRegex(@"\A0x(?:[0-9a-fA-F]{2})*\z")]
    private static partial Regex HexBytesRegex();
}

internal sealed class CorpusGzipStream : Stream
{
    private readonly GZipStream _stream;
    private readonly uint _expectedSize;
    private readonly string _path;
    private long _read;

    internal CorpusGzipStream(FileStream source)
    {
        _path = source.Name;
        if (source.Length < 18) throw new InvalidDataException("Truncated gzip corpus.");
        source.Seek(-4, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[4];
        source.ReadExactly(footer);
        _expectedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer);
        source.Position = 0;
        _stream = new GZipStream(source, CompressionMode.Decompress);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _stream.Read(buffer, offset, count);
        _read += read;
        // GZipStream accepts EOF before the trailer; a partial capture must not become a shorter corpus.
        if (read == 0 && count > 0 && unchecked((uint)_read) != _expectedSize) ValidateMembers(_path);
        return read;
    }
    private static void ValidateMembers(string path)
    {
        using var source = File.OpenRead(path);
        using var singleByte = new GzipMemberInput(source);
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[index] = value;
        }
        int Byte() => source.ReadByte() is var value && value >= 0 ? value : throw new InvalidDataException("Truncated gzip corpus.");
        var buffer = new byte[8192];
        Span<byte> footer = stackalloc byte[8];
        while (source.Position < source.Length)
        {
            var magic = Byte();
            if (magic == 0) continue;
            if (magic != 0x1f || Byte() != 0x8b || Byte() != 8) throw new InvalidDataException("Invalid gzip member.");
            var flags = Byte();
            for (var index = 0; index < 6; index++) Byte();
            if ((flags & 4) != 0) { var length = Byte() | (Byte() << 8); for (var index = 0; index < length; index++) Byte(); }
            if ((flags & 8) != 0) while (Byte() != 0) { }
            if ((flags & 16) != 0) while (Byte() != 0) { }
            if ((flags & 2) != 0) { Byte(); Byte(); }
            var crc = uint.MaxValue;
            uint size = 0;
            using (var deflate = new DeflateStream(singleByte, CompressionMode.Decompress, leaveOpen: true))
            {
                int read;
                while ((read = deflate.Read(buffer)) != 0)
                {
                    size = unchecked(size + (uint)read);
                    for (var index = 0; index < read; index++) crc = table[(crc ^ buffer[index]) & 255] ^ (crc >> 8);
                }
            }
            source.ReadExactly(footer);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer) != ~crc ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != size)
                throw new InvalidDataException("Invalid gzip trailer.");
        }
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
}

internal sealed class GzipMemberInput(Stream source) : Stream
{
    // One byte prevents DeflateStream from reading into the next member's header or trailer.
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(count, 1));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
