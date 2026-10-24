#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include CorpusParity.Helpers.cs
#:include RpcJson.Helpers.cs

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

var count = 0;
var root = Directory.CreateTempSubdirectory("corpus parity tests ");
var sentinel = "PRIVATE_CORPUS_SENTINEL";
var url = "http://fixture.invalid/rpc";
try
{
    foreach (var (input, expected) in new[] { ("250000", 250000), ("16", 16), ("250k", 10), ("0", 10), ("-1", 10), ("", 10), ("1.5", 10) })
        await Check($"environment cap {input}", () => { Equal(CorpusParity.EnvInt(input, 10), expected); return Task.CompletedTask; });
    foreach (var gzip in new[] { false, true }) await Check($"load plain/gzip {gzip}", () =>
    {
        using var parity = Create();
        Equal(parity.LoadCorpus(Corpus(2, gzip)).Length, 2);
        return Task.CompletedTask;
    });
    foreach (var line in new[] { "{nope PRIVATE_CORPUS_SENTINEL", "[]", "{\"method\":\"eth_getBalance\",\"params\":[]}", "{\"method\":\"eth_call\",\"params\":{}}", "", "{\"method\":\"eth_call\",\"params\":[NaN]}", "{\"method\":\"eth_call\",\"params\":[Infinity]}", "{\"method\":\"eth_call\",\"params\":[-Infinity]}" })
        await Check($"reject invalid corpus {count}", async () => { using var parity = Create(); await Reject(() => { parity.LoadCorpus(Write(line)); return Task.CompletedTask; }); });
    await Check("record cap and suffix", async () =>
    {
        using var parity = Create(values: [("RPC_BENCH_MAX_CORPUS_RECORDS", "1")]);
        await Reject(() => { parity.LoadCorpus(Corpus(2)); return Task.CompletedTask; }, "exceeds 1");
        await Reject(() => { parity.LoadCorpus(Path.Combine(root.FullName, "private.txt")); return Task.CompletedTask; }, "extension");
    });
    await Check("corrupt UTF8 stays private", async () =>
    {
        var path = Path.Combine(root.FullName, "invalid.jsonl.gz");
        using (var file = File.Create(path)) using (var gzip = new GZipStream(file, CompressionMode.Compress)) gzip.Write([0xff, 0xfe, .. Encoding.UTF8.GetBytes(sentinel)]);
        using var parity = Create(); await Reject(() => { parity.LoadCorpus(path); return Task.CompletedTask; }, "cannot read corpus");
    });
    await Check("truncated gzip stays private", async () =>
    {
        var path = Corpus(200, true); var bytes = File.ReadAllBytes(path); File.WriteAllBytes(path, bytes[..^8]);
        using var parity = Create(); await Reject(() => { parity.LoadCorpus(path); return Task.CompletedTask; });
    });
    await Check("concatenated gzip members remain loadable", () =>
    {
        var path = Corpus(2, true); var second = Corpus(3, true);
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. File.ReadAllBytes(second)]);
        using var parity = Create(); Equal(parity.LoadCorpus(path).Length, 5);
        return Task.CompletedTask;
    });
    foreach (var tracer in new[] { "callTracer", "prestateTracer", "4byteTracer", "stateGasTracer", "" }) await Check($"debug rewrite {tracer}", () =>
    {
        using var parity = Create(values: [("RPC_BENCH_CORPUS_METHOD", "debug_traceCall"), ("RPC_BENCH_CORPUS_TRACER", tracer)]);
        var result = parity.LoadCorpus(Write("{\"method\":\"eth_call\",\"params\":[{\"to\":\"0x1\"}]}"))[0];
        Equal(result[1]!.ToString(), "latest"); Equal(CorpusParity.Text(result[2]!["tracer"]), tracer);
        return Task.CompletedTask;
    });
    await Check("debug overrides and unchanged eth_call", () =>
    {
        var parameters = JsonNode.Parse("""[{"to":"0x1"},"0x123",{"a":{"balance":"0x1"}},{"number":"0x2"}]""")!.AsArray();
        var result = CorpusParity.RewriteRecord(parameters, "debug_traceCall", new JsonObject { ["tracer"] = "callTracer" });
        Equal(result[2]!["stateOverrides"]!.ToJsonString(), parameters[2]!.ToJsonString());
        Equal(result[2]!["blockOverrides"]!.ToJsonString(), parameters[3]!.ToJsonString());
        Equal(CorpusParity.RewriteRecord(parameters, "eth_call", []).ToJsonString(), parameters.ToJsonString());
        return Task.CompletedTask;
    });
    foreach (var (variable, value, message) in new[] { ("RPC_BENCH_CORPUS_TRACER", "callTracer2", "unknown tracer"), ("RPC_BENCH_CORPUS_METHOD", "trace_callMany", "unknown corpus method"), ("RPC_BENCH_CORPUS_TRACE_TYPES", "trace,bogus", "unknown trace type") })
        await Check(message, async () =>
        {
            using var parity = Create(values: [("RPC_BENCH_CORPUS_METHOD", variable.EndsWith("TYPES", StringComparison.Ordinal) ? "trace_call" : "debug_traceCall"), (variable, value)]);
            await Reject(() => { parity.Rewrite(); return Task.CompletedTask; }, message);
        });
    foreach (var includeOverride in new[] { false, true }) await Check($"parity trace rewrite override={includeOverride}", () =>
    {
        using var parity = Create(values: [("RPC_BENCH_CORPUS_METHOD", "trace_call"), ("RPC_BENCH_CORPUS_TRACE_TYPES", "trace,stateDiff")]);
        var parameters = JsonNode.Parse(includeOverride ? """[{"to":"0x1"},"0x123",{"a":{"balance":"0x1"}}]""" : """[{"to":"0x1"},null]""")!.AsArray();
        var (method, options) = parity.Rewrite(); var result = CorpusParity.RewriteRecord(parameters, method, options);
        Equal(result.Count, includeOverride ? 4 : 3); Equal(result[1]!.ToJsonString(), "[\"trace\",\"stateDiff\"]");
        Equal(result[2]!.ToString(), includeOverride ? "0x123" : "latest"); return Task.CompletedTask;
    });
    foreach (var method in new[] { "debug_traceCall", "trace_call" }) await Check($"rewrite invalid line {method}", async () =>
    {
        using var parity = Create(values: [("RPC_BENCH_CORPUS_METHOD", method)]);
        await Reject(() => { parity.LoadCorpus(Write(Record() + "\n{\"method\":\"eth_call\",\"params\":[]}")); return Task.CompletedTask; }, "line 2");
        if (method == "trace_call") await Reject(() => { parity.LoadCorpus(Write(Record() + "\n{\"method\":\"eth_call\",\"params\":[{},null,null,{}]}")); return Task.CompletedTask; }, "blockOverrides");
    });
    var state = Path.Combine(root.FullName, "state.json.gz");
    var report = Path.Combine(root.FullName, "parity.json");
    await Check("baseline compare fixed private schema", async () =>
    {
        var corpus = Corpus(3); using var parity = Create();
        await parity.Baseline(corpus, url, state, CancellationToken.None);
        Equal(await parity.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), true);
        var document = JsonNode.Parse(File.ReadAllText(report))!.AsObject();
        Equal(document["matched"]!.GetValue<int>(), 3); Equal(document["divergences"]!.AsArray().Count, 0);
        Equal(document.Select(pair => pair.Key).Order().SequenceEqual(CorpusParity.CounterFields.Concat(CorpusParity.LabelFields).Append("divergences").Order()), true);
        using var file = File.OpenRead(state); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new StreamReader(gzip);
        Private(reader.ReadToEnd()); Private(document.ToJsonString());
    });
    foreach (var (label, response, field) in new (string, Func<int, HttpResponseMessage>, string)[]
    {
        ("content", id => Result(id, "0xffff"), "content_mismatches"), ("baseline prefix", id => Result(id, "0xababcd"), "baseline_shorter"),
        ("candidate prefix", id => Result(id, "0x"), "candidate_shorter"), ("length", id => Result(id, "0xcccccc"), "length_mismatches"),
        ("rpc", id => Error(id, -32000), "candidate_rpc_errors"), ("http", _ => Raw("unavailable", 503), "candidate_transport_failures"),
        ("malformed", _ => Raw("private not json"), "candidate_invalid_responses"), ("id", _ => Result(999, "0xabab"), "candidate_invalid_responses"),
        ("type", id => Result(id, JsonValue.Create(7)), "candidate_invalid_responses"), ("hex", id => Result(id, "0xzz"), "candidate_invalid_responses"),
        ("non200 rpc", id => Error(id, -32000, 400), "candidate_rpc_errors")
    }) await Check($"classification {label}", async () =>
    {
        var corpus = Corpus(1); using (var baseline = Create()) await baseline.Baseline(corpus, url, state, CancellationToken.None);
        using var candidate = Create(request => response(request["id"]!.GetValue<int>()));
        Equal(await candidate.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), false);
        var document = JsonNode.Parse(File.ReadAllText(report))!; Equal(document[field]!.GetValue<int>(), 1); Equal(document["matched"]!.GetValue<int>(), 0);
        Equal(document["divergences"]![0]!["index"]!.GetValue<int>(), 1); Private(document.ToJsonString());
    });
    foreach (var allErrors in new[] { false, true }) await Check($"RPC agreement allErrors={allErrors}", async () =>
    {
        var corpus = Corpus(3);
        using var parity = Create(request => allErrors || request["id"]!.GetValue<int>() == 2 ? Error(request["id"]!.GetValue<int>(), -32000) : Result(request["id"]!.GetValue<int>(), "0xabab"));
        await parity.Baseline(corpus, url, state, CancellationToken.None);
        Equal(await parity.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), true);
        Equal(JsonNode.Parse(File.ReadAllText(report))!["both_rpc_errors"]!.GetValue<int>(), allErrors ? 3 : 1);
        using var clean = Create(); Equal(await clean.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), false);
        Equal(JsonNode.Parse(File.ReadAllText(report))!["baseline_rpc_errors"]!.GetValue<int>(), allErrors ? 3 : 1);
    });
    await Check("baseline transport failure writes no state", async () =>
    {
        File.Delete(state); using var parity = Create(request => request["id"]!.GetValue<int>() == 2 ? Raw("unavailable", 503) : Result(request["id"]!.GetValue<int>(), "0xabab"));
        await Reject(() => parity.Baseline(Corpus(3), url, state, CancellationToken.None), "transport_failure=1"); Equal(File.Exists(state), false);
    });
    foreach (var mismatch in new[] { "head", "hash", "count", "missing" }) await Check($"identity/state mismatch {mismatch}", async () =>
    {
        var corpus = Corpus(2); using (var parity = Create()) await parity.Baseline(corpus, url, state, CancellationToken.None);
        File.Delete(report);
        using var candidate = Create(head: mismatch == "head" ? 101 : 100, hash: mismatch == "hash" ? "0x" + new string('a', 64) : null);
        await Reject(() => candidate.Compare(mismatch == "count" ? Corpus(3) : corpus, url, mismatch == "missing" ? state + ".missing" : state, report, "base", "candidate", null, CancellationToken.None));
        Equal(File.Exists(report), false);
    });
    await Check("serial retry recovers initial transport artifacts", async () =>
    {
        var corpus = Corpus(4); using (var parity = Create()) await parity.Baseline(corpus, url, state, CancellationToken.None);
        var attempts = new int[5];
        using var flaky = Create(request => Interlocked.Increment(ref attempts[request["id"]!.GetValue<int>()]) == 1 ? Raw("busy", 503) : Result(request["id"]!.GetValue<int>(), "0xabab"));
        Equal(await flaky.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), true); Equal(attempts.Skip(1).All(value => value == 2), true);
    });
    await Check("disagreement gets second serial gate", async () =>
    {
        var corpus = Corpus(2); using (var parity = Create()) await parity.Baseline(corpus, url, state, CancellationToken.None);
        var attempts = new int[3];
        using var flaky = Create(request => Result(request["id"]!.GetValue<int>(), Interlocked.Increment(ref attempts[request["id"]!.GetValue<int>()]) == 1 ? "0xdead" : "0xabab"));
        Equal(await flaky.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), true);
    });
    await Check("persistent disagreements survive retry and previews are capped", async () =>
    {
        var corpus = Corpus(4); using (var parity = Create()) await parity.Baseline(corpus, url, state, CancellationToken.None);
        using var wrong = Create(request => Result(request["id"]!.GetValue<int>(), "0xdead"), [("RPC_BENCH_MAX_DIVERGENCE_INDEXES", "2")]);
        Equal(await wrong.Compare(corpus, url, state, report, "base", "candidate", Path.Combine(root.FullName, "diffs.json"), CancellationToken.None), false);
        var document = JsonNode.Parse(File.ReadAllText(report))!; Equal(document["content_mismatches"]!.GetValue<int>(), 4); Equal(document["divergences"]!.AsArray().Count, 2);
    });
    await Check("bounded word differences reveal no operands", () =>
    {
        var words = CorpusParity.MaxDiffWords + 4;
        var described = CorpusParity.DescribeDivergence(1, "0x" + string.Concat(Enumerable.Range(0, words).Select(value => value.ToString("x64"))), "0x" + string.Concat(Enumerable.Range(1, words).Select(value => value.ToString("x64"))));
        Equal(described["total_differing_words"]!.GetValue<int>(), words); Equal(described["differing_words"]!.AsArray().Count, CorpusParity.MaxDiffWords);
        foreach (var word in described["differing_words"]!.AsArray()) Equal(word!.AsObject().All(pair => pair.Key is "word" or "direction"), true);
        foreach (var reverse in new[] { false, true })
        {
            var secret = new string('d', 64); var zero = new string('0', 64);
            var diff = CorpusParity.DescribeDivergence(1, "0x" + (reverse ? secret : zero), "0x" + (reverse ? zero : secret));
            Equal(diff["differing_words"]!.ToJsonString(), "[{\"word\":0,\"direction\":\"" + (reverse ? "lower" : "higher") + "\"}]"); Equal(diff.ToJsonString().Contains(secret), false);
        }
        return Task.CompletedTask;
    });
    foreach (var (json, digest) in new[]
    {
        ("""{"html":"<>&'","unicode":"é😀"}""", "20e197fdee72458c5b6959f2c26bbadb6c453fd2bb8fdd61ac46292dafe66dac"),
        ("""{"n":1e0,"small":1.0e-7,"big":1.0e16}""", "65bb61e856aadd01a93cca06d4b32cbbf820ffce15b244af3e8acbd1367e70d5"),
        ("""{"n":1.0,"small":0.0000001,"big":10000000000000000.0}""", "65bb61e856aadd01a93cca06d4b32cbbf820ffce15b244af3e8acbd1367e70d5"),
        ("""{"n":-0}""", "f3013f933b9fb80ab6d995e7ad9da36f683837ba1d81e950c943d40111eac2f0"),
        ("""{"\ud800\udc00":1,"\ue000":2}""", "e435664447ae90b0ae08eea493d3808d1143a0a0894ea1df55f14a290519725e"),
        ("""{"z":[true,null,"\n\t"],"a":-0.0}""", "488d68989b7e7ce27b9c41dd099a933657b83ba6ed87913a76c87a2fa6c793d2")
    }) await Check("trace digest matches Python canonical JSON " + digest[..8], async () =>
    {
        using var parity = Create(_ => Raw("{\"id\":1,\"result\":" + json + "}"));
        var result = await parity.Post(url, 1, [], "debug_traceCall", CancellationToken.None);
        Equal(result.Category, null); Equal(result.Result, "0x" + digest);
    });
    foreach (var method in new[] { "debug_traceCall", "trace_call" }) await Check("deep trace " + method, async () =>
    {
        var json = "{}";
        for (var depth = 0; depth < 40; depth++) json = "{\"calls\":[" + json + "]}";
        using var parity = Create(_ => Raw("{\"id\":1,\"result\":" + json + "}"));
        var result = await parity.Post(url, 1, [], method, CancellationToken.None);
        Equal(result.Category, null);
        Equal(result.Result, "0x" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    });
    foreach (var (json, expected) in new[] { ("1e15", "1000000000000000.0"), ("1000000000000001.0", "1000000000000001.0"), ("1e-4", "0.0001"), ("1e16", "1e+16") })
        await Check("float canonical form " + json, () =>
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Equal(RpcJson.Canonical(document.RootElement), expected);
            return Task.CompletedTask;
        });
    await Check("corrupt gzip header stays private", async () =>
    {
        var path = Path.Combine(root.FullName, "bad-header.jsonl.gz"); File.WriteAllText(path, sentinel);
        using var parity = Create(); await Reject(() => { parity.LoadCorpus(path); return Task.CompletedTask; }, "cannot read corpus");
    });
    await Check("trace rejects non-finite numbers", async () =>
    {
        using var parity = Create(_ => Raw("{\"id\":1,\"result\":1e999}"));
        Equal((await parity.Post(url, 1, [], "debug_traceCall", CancellationToken.None)).Category, "invalid_response");
    });
    foreach (var method in new[] { "debug_traceCall", "trace_call" })
    {
        await Check($"trace canonical digest method/params {method}", async () =>
        {
            JsonObject? sent = null;
            using var parity = Create(request => { sent = request.DeepClone().AsObject(); return Result(request["id"]!.GetValue<int>(), JsonNode.Parse("{\"type\":\"CALL\",\"gasUsed\":\"0x1\"}")); }, [("RPC_BENCH_CORPUS_METHOD", method)]);
            var corpus = Corpus(1); var record = parity.LoadCorpus(corpus)[0];
            var (category, outcome) = await parity.Post(url, 1, record, method, CancellationToken.None);
            Equal(category, null); Equal(outcome.Length, 66); Equal(sent!["method"]!.ToString(), method); Equal(sent["params"]!.ToJsonString(), record.ToJsonString());
            await parity.Baseline(corpus, url, state, CancellationToken.None);
            using var reordered = Create(request => Result(request["id"]!.GetValue<int>(), JsonNode.Parse("{\"gasUsed\":\"0x1\",\"type\":\"CALL\"}")), [("RPC_BENCH_CORPUS_METHOD", method)]);
            Equal(await reordered.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), true);
            using var changed = Create(request => Result(request["id"]!.GetValue<int>(), JsonNode.Parse("{\"gasUsed\":\"0x2\"}")), [("RPC_BENCH_CORPUS_METHOD", method)]);
            Equal(await changed.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), false);
            await Reject(() => changed.Compare(corpus, url, state, report, "base", "candidate", "diff.json", CancellationToken.None), "--diffs");
        });
        await Check($"trace probe stops parsing after success {method}", async () =>
        {
            var requests = 0;
            using var parity = Create(request => { requests++; return Result(request["id"]!.GetValue<int>(), new JsonObject()); }, [("RPC_BENCH_CORPUS_METHOD", method)]);
            await parity.Probe(Write(Record() + "\n{not json"), url, CancellationToken.None); Equal(requests, 1);
        });
        foreach (var httpStatus in new[] { 200, 404 }) await Check($"trace probe advances ordinary errors {method}/{httpStatus}", async () =>
        {
            var requests = 0;
            using var parity = Create(request => { requests++; return request["id"]!.GetValue<int>() == 1 ? Error(1, -32000, httpStatus) : Result(2, new JsonObject()); }, [("RPC_BENCH_CORPUS_METHOD", method)]);
            await parity.Probe(Corpus(2), url, CancellationToken.None); Equal(requests, 2);
        });
        foreach (var code in new[] { -32600, -32601, -32000 }) foreach (var httpStatus in new[] { 200, 404 })
            await Check($"trace capability rejection {method}/{code}/{httpStatus}", async () =>
            {
                var requests = 0;
                using var parity = Create(request => { requests++; return Error(request["id"]!.GetValue<int>(), code, httpStatus); }, [("RPC_BENCH_CORPUS_METHOD", method)]);
                await Reject(() => parity.Probe(Corpus(2), url, CancellationToken.None), code == -32000 ? "no successful results" : $"rpc_error:{code}");
                Equal(requests, code == -32000 ? 2 : 1);
            });
        foreach (var kind in new[] { "missing code", "transport", "oversize" }) await Check($"trace probe failure {method}/{kind}", async () =>
        {
            using var parity = Create(request => kind switch
            {
                "missing code" => Raw($"{{\"id\":{request["id"]},\"error\":{{\"message\":\"{sentinel}\"}}}}"),
                "transport" => Raw(sentinel, 503), _ => Raw(new string('x', 64))
            }, kind == "oversize" ? [("RPC_BENCH_CORPUS_METHOD", method), ("RPC_BENCH_MAX_RESPONSE_BYTES", "8")] : [("RPC_BENCH_CORPUS_METHOD", method)]);
            await Reject(() => parity.Probe(Corpus(1), url, CancellationToken.None), kind == "oversize" ? "increase tool_config.max_response_bytes" : kind == "transport" ? "transport_failure" : "rpc_error");
        });
        foreach (var allErrors in new[] { true, false }) await Check($"trace all-error gate {method}/{allErrors}", async () =>
        {
            var corpus = Corpus(2); File.Delete(state);
            using var parity = Create(request => allErrors || request["id"]!.GetValue<int>() == 2 ? Error(request["id"]!.GetValue<int>(), -32000) : Result(1, new JsonObject()), [("RPC_BENCH_CORPUS_METHOD", method)]);
            if (allErrors)
            {
                await Reject(() => parity.Baseline(corpus, url, state, CancellationToken.None), "no successful results"); Equal(File.Exists(state), false);
                using var eth = Create(request => Error(request["id"]!.GetValue<int>(), -32000));
                await eth.Baseline(corpus, url, state, CancellationToken.None);
            }
            else await parity.Baseline(corpus, url, state, CancellationToken.None);
            Equal(await parity.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), !allErrors);
        });
        foreach (var command in new[] { "baseline", "compare", "timings" }) foreach (var code in new[] { -32600, -32000 })
            await Check($"CLI probe before output {method}/{command}/{code}", async () =>
            {
                var output = Path.Combine(root.FullName, "not-written"); File.Delete(output);
                using var parity = Create(request => Error(request["id"]!.GetValue<int>(), code), [("RPC_BENCH_CORPUS_METHOD", method)]);
                var arguments = new List<string> { command, "--corpus", Corpus(1), "--rpc-url", url };
                arguments.AddRange(command == "timings" ? ["--out", output] : command == "baseline" ? ["--state", output] : ["--state", state, "--report", output, "--baseline-client", "base", "--candidate-client", "candidate"]);
                Equal(await parity.Main(arguments.ToArray(), CancellationToken.None), 2); Equal(File.Exists(output), false);
            });
    }
    await Check("oversize replay classified invalid", async () =>
    {
        var corpus = Corpus(1); using (var parity = Create()) await parity.Baseline(corpus, url, state, CancellationToken.None);
        using var oversized = Create(_ => Raw(new string('x', 64)), [("RPC_BENCH_MAX_RESPONSE_BYTES", "8")]);
        Equal(await oversized.Compare(corpus, url, state, report, "base", "candidate", null, CancellationToken.None), false);
        Equal(JsonNode.Parse(File.ReadAllText(report))!["candidate_invalid_responses"]!.GetValue<int>(), 1);
    });
    foreach (var failures in new[] { false, true }) await Check($"timing matrix and metadata failures={failures}", async () =>
    {
        var output = Path.Combine(root.FullName, "timings.csv");
        using var parity = Create(request => failures && request["id"]!.GetValue<int>() == 2 ? Error(2, -32000) : Result(request["id"]!.GetValue<int>(), "0xab"));
        var console = Console.Out; using var messages = new StringWriter();
        try { Console.SetOut(messages); await parity.Timings(Corpus(3), url, output, 4, 0, 4, 3, 17, CancellationToken.None); }
        finally { Console.SetOut(console); }
        Equal(messages.ToString().Contains("did not return a result", StringComparison.Ordinal), failures);
        var lines = File.ReadAllLines(output); Equal(lines.Length, 4);
        Equal(lines[0], "record_index,pass_1_ms,pass_1_status,pass_2_ms,pass_2_status,pass_3_ms,pass_3_status,pass_4_ms,pass_4_status");
        for (var index = 1; index < lines.Length; index++)
        {
            var cells = lines[index].Split(','); Equal(cells[0], index.ToString()); Equal(cells.Length, 9);
            for (var pass = 0; pass < 4; pass++) { Equal(double.Parse(cells[1 + 2 * pass], System.Globalization.CultureInfo.InvariantCulture) >= 0, true); Equal(cells[2 + 2 * pass], failures && index == 2 ? "rpc_error:-32000" : "ok"); }
        }
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "timings.meta.json")))!;
        Equal(metadata["warmup_seconds"]!.GetValue<int>(), 3); Equal(metadata["warmup_rps"]!.GetValue<double>(), 17d); Equal(metadata["requests"]!.GetValue<int>(), 12);
        Private(string.Join('\n', lines)); Private(metadata.ToJsonString());
    });
    await Check("timing pacing target", async () =>
    {
        using var parity = Create(); var start = Stopwatch.GetTimestamp();
        var console = Console.Out; using var messages = new StringWriter();
        try { Console.SetOut(messages); await parity.Timings(Corpus(5), url, Path.Combine(root.FullName, "paced.csv"), 4, 40, 8, 0, 0, CancellationToken.None); }
        finally { Console.SetOut(console); }
        Equal(messages.ToString().Contains("target 40", StringComparison.Ordinal), true);
        Equal(Stopwatch.GetElapsedTime(start).TotalSeconds >= .4, true);
    });
    await Check("validate CLI no network and content-free errors", async () =>
    {
        using var parity = Create(_ => throw new Exception("validate must not request"));
        Equal(await parity.Main(["validate", "--corpus", Corpus(2)], CancellationToken.None), 0);
        Equal(await parity.Main(["validate", "--corpus", Write("{private")], CancellationToken.None), 2);
    });
    await Check("CLI rejects unknown and missing arguments before network", async () =>
    {
        using var parity = Create(_ => throw new Exception("invalid CLI must not request"), [("RPC_BENCH_RPC_METHOD", "debug_traceCall")]);
        Equal(await parity.Main(["validate", "--corpus", Corpus(1), "--typo", "x"], CancellationToken.None), 2);
        Equal(await parity.Main(["compare", "--corpus", Corpus(1), "--rpc-url", url], CancellationToken.None), 2);
    });
    await Check("transport retries once", async () =>
    {
        var calls = 0; using var parity = Create(request => { if (++calls == 1) throw new HttpRequestException(sentinel); return Result(request["id"]!.GetValue<int>(), "0xab"); });
        var result = await parity.Post(url, 1, [], "eth_call", CancellationToken.None); Equal(result.Category, null); Equal(calls, 2);
    });
    Console.WriteLine($"Passed {count} tests."); return 0;
}
finally { root.Delete(recursive: true); }

async Task Check(string name, Func<Task> test) { await test(); Console.WriteLine($"PASS {name}"); count++; }
static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
void Private(string text) { if (text.Contains(sentinel, StringComparison.Ordinal)) throw new Exception("Private corpus data leaked"); }
async Task Reject(Func<Task> action, string message = "")
{
    try { await action(); }
    catch (CorpusParityError error) { Private(error.Message); if (!error.Message.Contains(message, StringComparison.Ordinal)) throw new Exception($"Expected {message}, got {error.Message}"); return; }
    throw new Exception("Expected rejection");
}
string Record() => $"{{\"method\":\"eth_call\",\"params\":[{{\"to\":\"0x1\",\"data\":\"{sentinel}\"}},\"latest\"]}}";
string Corpus(int records, bool gzip = false) => Write(string.Join('\n', Enumerable.Repeat(Record(), records)) + "\n", gzip);
string Write(string text, bool compressed = false)
{
    var path = Path.Combine(root.FullName, $"corpus-{Guid.NewGuid():N}.jsonl" + (compressed ? ".gz" : ""));
    if (compressed) { using var file = File.Create(path); using var gzip = new GZipStream(file, CompressionMode.Compress); using var writer = new StreamWriter(gzip); writer.Write(text); }
    else File.WriteAllText(path, text);
    return path;
}
CorpusParity Create(Func<JsonObject, HttpResponseMessage>? response = null, (string, string)[]? values = null, int head = 100, string? hash = null)
{
    var environment = new Dictionary<string, string>(); foreach (var (key, value) in values ?? []) environment[key] = value;
    return new CorpusParity(environment.GetValueOrDefault, new RpcFixture(request =>
    {
        var id = request["id"]!.GetValue<int>();
        return request["method"]!.ToString() switch
        {
            "eth_chainId" => Result(id, "0x1"),
            "eth_getBlockByNumber" => Result(id, new JsonObject { ["number"] = $"0x{head:x}", ["hash"] = hash ?? "0x" + new string('b', 64) }),
            _ => response?.Invoke(request) ?? Result(id, "0xabab")
        };
    }));
}
static HttpResponseMessage Result(int id, JsonNode? result) => Raw(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
static HttpResponseMessage Error(int id, int code, int status = 200) => Raw(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = "PRIVATE_CORPUS_SENTINEL" } }.ToJsonString(), status);
static HttpResponseMessage Raw(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };

internal sealed class RpcFixture(Func<JsonObject, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        response(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
}
