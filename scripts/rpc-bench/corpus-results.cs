#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include corpus-parity.helpers.cs
#:include rpc-json.helpers.cs

using Microsoft.VisualBasic.FileIO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

#if !SCRIPT_TEST
try
{
    if (args.Length == 3 && args[0] == "sanitize") CorpusResults.Sanitize(args[1], args[2]);
    else if (args.Length == 3 && args[0] == "stage") CorpusResults.Stage(args[1], args[2]);
    else if (args.Length >= 6 && args[0] == "comment")
    {
        string? baseline = null, candidate = null;
        var cached = false;
        for (var index = 2; index < args.Length; index++)
            switch (args[index])
            {
                case "--baseline" when index + 1 < args.Length: baseline = args[++index]; break;
                case "--candidate" when index + 1 < args.Length: candidate = args[++index]; break;
                case "--cached-baseline": cached = true; break;
                default: throw new ArgumentException("unknown or incomplete argument");
            }
        if (baseline is null || candidate is null) throw new ArgumentException("--baseline and --candidate are required");
        Console.WriteLine(CorpusResults.Comment(args[1], baseline, candidate, cached));
    }
    else throw new ArgumentException("usage: corpus-results.cs sanitize RAW OUT | stage OUTPUT STAGE | comment STAGE --baseline LABEL --candidate LABEL [--cached-baseline]");
    return 0;
}
catch (ArgumentException error) { Console.Error.WriteLine($"error: {error.Message}"); return 2; }
catch (Exception error) when (error is CorpusResultsError or IOException or JsonException) { Console.Error.WriteLine($"error: {error.Message}"); return 1; }
#endif

internal sealed class CorpusResultsError(string message) : Exception(message);

internal static partial class CorpusResults
{
    internal const int BaselineSchema = 1;
    internal static readonly Dictionary<string, string[]> MetricFields = new()
    {
        ["http_req_duration"] = ["avg", "med", "p(90)", "p(95)", "p(99)", "max"], ["http_reqs"] = ["count", "rate"],
        ["http_req_failed"] = ["rate"], ["checks"] = ["passes", "fails"], ["dropped_iterations"] = ["count"]
    };
    private static readonly string[] ClassFields = [.. MetricFields["http_req_duration"], "count"];
    internal static readonly string[] StagedFilenames = ["summary.json", "parity.json", "jsonbench-summary.md", "summaries.manifest", "timings.csv", "parity-diffs.json", "timings.meta.json", "resources.json"];
    private static readonly HashSet<string> ResourceFields = ["wall_seconds", "samples", "cpu_seconds", "cpu_avg_cores", "cpu_peak_cores", "cpu_throttled_usec", "memory_avg_bytes", "memory_peak_bytes", "memory_anon_avg_bytes", "memory_anon_peak_bytes", "memory_file_avg_bytes", "io_read_bytes", "io_write_bytes", "stall_cpu_usec", "stall_io_usec", "stall_memory_usec", "requests", "cpu_ms_per_request", "io_read_bytes_per_request"];
    private static readonly (string Key, string Name)[] CommentMetrics = [("avg", "avg"), ("med", "median"), ("p(90)", "p90"), ("p(95)", "p95"), ("p(99)", "p99"), ("max", "max")];
    private static bool Shape(JsonNode? node, IEnumerable<string> keys) => node is JsonObject obj && obj.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(keys);
    private static bool Subset(JsonNode? node, IEnumerable<string> keys) => node is JsonObject obj && obj.All(pair => keys.Contains(pair.Key));
    private static bool IsInteger(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out _);
    private static double Numeric(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !(value.TryGetValue<double>(out var result) || double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)))
            throw new CorpusResultsError("value is not numeric");
        return result;
    }
    private static double Number(JsonNode? node, string label)
    {
        double number;
        try { number = Numeric(node); }
        catch (CorpusResultsError) { throw new CorpusResultsError($"{label} is not a finite non-negative number"); }
        if (!double.IsFinite(number) || number < 0) throw new CorpusResultsError($"{label} is not a finite non-negative number");
        return number;
    }
    private static bool ShortString(JsonNode? node, int length) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && text.Length <= length;
    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new CorpusResultsError($"{Path.GetFileName(path)} is not an object");
    private static JsonNode MetricValue(JsonNode? metric, string field, string label)
    {
        if (metric is JsonObject obj)
        {
            var values = obj["values"] as JsonObject;
            if (values?.TryGetPropertyValue(field, out var value) == true || obj.TryGetPropertyValue(field, out value) ||
                field == "rate" && (values?.TryGetPropertyValue("value", out value) == true || obj.TryGetPropertyValue("value", out value)))
            { Number(value, label); return value!.DeepClone(); }
        }
        throw new CorpusResultsError($"missing metric value {label}");
    }

    internal static JsonObject SanitizeData(JsonNode? raw)
    {
        if (raw is not JsonObject obj || obj["metrics"] is not JsonObject metrics) throw new CorpusResultsError("summary has no metrics object");
        var sanitized = new JsonObject();
        foreach (var (name, fields) in MetricFields)
        {
            var values = new JsonObject();
            foreach (var field in fields) values[field] = name is "dropped_iterations" or "checks" or "http_req_failed" && !metrics.ContainsKey(name) ? JsonValue.Create(0) : MetricValue(metrics[name], field, $"{name}.{field}");
            sanitized[name] = new JsonObject { ["values"] = values };
        }
        if (Numeric(sanitized["http_reqs"]!["values"]!["count"]) < 1) throw new CorpusResultsError("summary reports zero requests");
        var classes = new Dictionary<string, JsonObject>();
        foreach (var (key, metric) in metrics)
        {
            var match = TaggedMetricRegex().Match(key);
            var tag = match.Success ? ClassTagRegex().Match(match.Groups[2].Value) : Match.Empty;
            if (!tag.Success) continue;
            var name = tag.Groups[1].Value;
            if (!classes.TryGetValue(name, out var values)) classes[name] = values = [];
            if (match.Groups[1].Value == "http_req_duration")
                foreach (var field in MetricFields["http_req_duration"]) values[field] = MetricValue(metric, field, $"{name}.{field}");
            else values["count"] = MetricValue(metric, "count", $"{name}.count");
        }
        var complete = new JsonObject();
        foreach (var (name, values) in classes.OrderBy(pair => long.Parse(pair.Key[6..], CultureInfo.InvariantCulture)))
        {
            if (!MetricFields["http_req_duration"].All(values.ContainsKey)) continue;
            if (!values.ContainsKey("count")) values["count"] = 0;
            complete[name] = new JsonObject { ["values"] = values };
        }
        if (complete.Count > 0) sanitized["classes"] = complete;
        return new JsonObject { ["metrics"] = sanitized };
    }

    internal static void Sanitize(string rawPath, string output)
    {
        JsonNode? raw;
        try { raw = JsonNode.Parse(File.ReadAllText(rawPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { throw new CorpusResultsError("raw summary is missing or not valid JSON"); }
        CorpusParity.Save(output, SanitizeData(raw));
    }

    internal static void Validate(string path)
    {
        var name = Path.GetFileName(path);
        if (name == "timings.csv") { ValidateTimings(path); return; }
        var data = Read(path);
        void Require(bool condition, string message) { if (!condition) throw new CorpusResultsError($"{name}: {message}"); }
        void Integer(string key, bool nonnegative = true) => Require(IsInteger(data[key]) && (!nonnegative || Numeric(data[key]) >= 0), $"{key} is not a non-negative integer");
        switch (name)
        {
            case "summary.json":
                Require(Shape(data, ["metrics"]) && Subset(data["metrics"], MetricFields.Keys.Append("classes")), "is not a sanitized summary");
                var metrics = data["metrics"]!.AsObject();
                foreach (var (metricName, fields) in MetricFields)
                {
                    Require(metrics[metricName] is JsonObject metric && metric["values"] is JsonObject, $"metric {metricName} has an unexpected shape");
                    foreach (var field in fields) Number(metrics[metricName]!["values"]![field], $"{metricName}.{field}");
                }
                if (metrics["classes"] is not null)
                {
                    Require(metrics["classes"] is JsonObject, "classes is not an object");
                    foreach (var (className, metric) in metrics["classes"]!.AsObject())
                    {
                        Require(ClassNameRegex().IsMatch(className) && metric is JsonObject obj && Shape(obj["values"], ClassFields), "class entry has an unexpected shape");
                        foreach (var field in ClassFields) Number(metric!["values"]![field], $"{className}.{field}");
                    }
                }
                break;
            case "parity.json":
                Require(Shape(data, CorpusParity.CounterFields.Concat(CorpusParity.LabelFields).Append("divergences")), "does not match the parity report schema");
                foreach (var key in CorpusParity.CounterFields) Integer(key);
                foreach (var key in CorpusParity.LabelFields) Require(ShortString(data[key], 128), $"{key} is not a short string");
                Require(data["divergences"] is JsonArray array && array.Count <= Numeric(data["total"]), "divergences exceed the record count");
                foreach (var entry in data["divergences"]!.AsArray()) Require(Shape(entry, ["index", "kind"]) && IsInteger(entry!["index"]) && Numeric(entry["index"]) >= 1 && ShortString(entry["kind"], 64), "divergences entries must be index/kind pairs");
                break;
            case "parity-diffs.json":
                Require(Shape(data, ["baseline_client", "candidate_client", "total_divergences", "recorded", "diffs"]), "does not match the diffs schema");
                foreach (var key in CorpusParity.LabelFields) Require(ShortString(data[key], 128), $"{key} is not a short string");
                Integer("total_divergences"); Integer("recorded");
                Require(data["diffs"] is JsonArray diffs && diffs.Count == Numeric(data["recorded"]), "diffs length does not match 'recorded'");
                foreach (var entry in data["diffs"]!.AsArray())
                {
                    Require(Subset(entry, ["index", "baseline_bytes", "candidate_bytes", "baseline_all_zero", "candidate_all_zero", "differing_words", "total_differing_words"]) && entry!.AsObject().ContainsKey("index"), "divergence entry has unexpected fields");
                    var obj = entry!.AsObject();
                    foreach (var key in new[] { "index", "baseline_bytes", "candidate_bytes", "total_differing_words" }) if (obj.ContainsKey(key)) Require(IsInteger(obj[key]), $"{key} is not an integer");
                    foreach (var key in new[] { "baseline_all_zero", "candidate_all_zero" }) if (obj.ContainsKey(key)) Require(obj[key] is JsonValue boolean && boolean.TryGetValue<bool>(out _), $"{key} is not a boolean");
                    if (obj.ContainsKey("differing_words"))
                    {
                        Require(obj["differing_words"] is JsonArray, "differing_words is not an array");
                        foreach (var word in obj["differing_words"]!.AsArray()) Require(Subset(word, ["word", "direction"]) && IsInteger(word!["word"]) && (!word.AsObject().ContainsKey("direction") || CorpusParity.Text(word["direction"]) is "higher" or "lower"), "a differing word carries unexpected fields");
                    }
                }
                break;
            case "timings.meta.json":
                Require(Shape(data, ["head", "chain_id", "block_hash", "records", "passes", "requests", "target_rps", "achieved_rps", "concurrency", "warmup_seconds", "warmup_rps", "outcomes"]), "does not match the timings metadata schema");
                foreach (var key in new[] { "head", "chain_id", "records", "passes", "requests", "concurrency", "warmup_seconds" }) Integer(key);
                foreach (var key in new[] { "target_rps", "achieved_rps", "warmup_rps" }) Number(data[key], key);
                Require(data["block_hash"] is JsonValue hash && hash.TryGetValue<string>(out var text) && BlockHashRegex().IsMatch(text), "block_hash is not a 32-byte hex hash");
                Require(data["outcomes"] is JsonObject, "outcomes is not an object");
                foreach (var (key, value) in data["outcomes"]!.AsObject()) Require(StatusRegex().IsMatch(key) && IsInteger(value), "unexpected outcome entry");
                break;
            case "resources.json":
                Require(Subset(data, ResourceFields), "does not match the resource schema");
                foreach (var (key, value) in data) if (value is not null) { try { Numeric(value); } catch (CorpusResultsError) { throw new CorpusResultsError($"{name}: {key} is not numeric"); } }
                break;
        }
    }

    private static IEnumerable<string[]> Csv(string path)
    {
        using var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        while (!parser.EndOfData) yield return parser.ReadFields()!;
    }

    private static void ValidateTimings(string path)
    {
        using var rows = Csv(path).GetEnumerator();
        if (!rows.MoveNext()) throw new CorpusResultsError("timings.csv: empty");
        var header = rows.Current;
        if (header.Length < 2 || header[0] != "record_index") throw new CorpusResultsError("timings.csv: unexpected header");
        var numeric = new HashSet<int> { 0 };
        for (var index = 1; index < header.Length; index++)
            if (header[index].StartsWith("pass_", StringComparison.Ordinal) && header[index].EndsWith("_ms", StringComparison.Ordinal)) numeric.Add(index);
            else if (!(header[index].StartsWith("pass_", StringComparison.Ordinal) && header[index].EndsWith("_status", StringComparison.Ordinal))) throw new CorpusResultsError("timings.csv: unexpected column name");
        var number = 1;
        while (rows.MoveNext())
        {
            number++; var row = rows.Current;
            if (row.Length != header.Length) throw new CorpusResultsError($"timings.csv: row {number} has {row.Length} of {header.Length} columns");
            for (var index = 0; index < row.Length; index++)
            {
                if (row[index].Length == 0) continue;
                if (numeric.Contains(index))
                { if (!double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) throw new CorpusResultsError($"timings.csv: row {number} holds a non-numeric value"); }
                else if (!StatusRegex().IsMatch(row[index])) throw new CorpusResultsError($"timings.csv: row {number} holds an unexpected status");
            }
        }
    }

    internal static void Stage(string outputRoot, string stageRoot)
    {
        var source = Resolve(outputRoot);
        var destination = Resolve(stageRoot);
        if (!Directory.Exists(source)) throw new CorpusResultsError("output root does not exist");
        if (Within(destination, source) || Within(source, destination) || destination == Path.GetPathRoot(destination)) throw new CorpusResultsError("stage root must be separate from the output root");
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        var staged = 0;
        foreach (var path in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (!StagedFilenames.Contains(name)) continue;
            var relative = Path.GetRelativePath(source, path);
            if (relative.Split(Path.DirectorySeparatorChar)[0].StartsWith("warmup", StringComparison.Ordinal) || Path.GetFileName(Path.GetDirectoryName(path))!.StartsWith("warmup", StringComparison.Ordinal)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                if (name == "summaries.manifest") { if (StageManifest(path, source, target)) staged++; continue; }
                if (name != "jsonbench-summary.md") Validate(path);
            }
            catch (Exception exception) when (exception is IOException or JsonException or System.Text.DecoderFallbackException or MalformedLineException)
            { throw new CorpusResultsError($"{name}: unreadable ({exception.GetType().Name})"); }
            File.Copy(path, target, overwrite: true);
            staged++;
        }
        if (staged == 0) throw new CorpusResultsError("no publishable result files found under the output root");
        Console.WriteLine($"staged {staged} aggregate file(s)");
    }

    private static bool StageManifest(string path, string source, string target)
    {
        var lines = new List<string>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = ManifestRegex().Match(line);
            if (!match.Success) { Console.Error.WriteLine($"::warning::summaries.manifest: line {number} does not match the manifest shape — index not staged"); return false; }
            var resolved = Resolve(match.Groups["path"].Value);
            if (!Within(source, resolved)) { Console.Error.WriteLine($"::warning::summaries.manifest: line {number} points outside the output root — index not staged"); return false; }
            lines.Add($"{match.Groups["prefix"].Value}={Path.GetRelativePath(source, resolved).Replace('\\', '/')}");
        }
        File.WriteAllText(target, string.Join('\n', lines) + "\n"); return true;
    }

    private static string Resolve(string path)
    {
        var absolute = Path.GetFullPath(path);
        var current = Path.GetPathRoot(absolute)!;
        foreach (var part in absolute[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
    private static string Family(string label) => RoundSuffixRegex().Replace(label, "");
    private static double? Spread(double[] values) => values.Length < 2 || values.Average() == 0 ? null : (values.Max() - values.Min()) / values.Average() * 100;
    private static double Delta(double baseline, double candidate) => baseline != 0 ? (candidate - baseline) / baseline * 100 : double.NaN;
    private static string Arrow(double delta, double floor) => double.IsNaN(delta) ? "⚪" : delta < -floor ? "🟢" : delta > floor ? "🔴" : "⚪";
    private static string Fixed(double value, int decimals = 2) => value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
    private static string Signed(double value) => double.IsNaN(value) ? "+nan" : (double.IsNegative(value) ? "-" : "+") + Math.Abs(value).ToString("F1", CultureInfo.InvariantCulture);
    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }
    private static Dictionary<int, List<double>> LoadTimings(string path)
    {
        var result = new Dictionary<int, List<double>>();
        using var rows = Csv(path).GetEnumerator();
        if (!rows.MoveNext()) return result;
        var header = rows.Current;
        while (rows.MoveNext())
        {
            var row = rows.Current;
            var index = int.Parse(row[0], CultureInfo.InvariantCulture);
            if (!result.TryGetValue(index, out var values)) result[index] = values = [];
            for (var position = 1; position < header.Length - 1; position += 2)
                if (row[position].Length > 0 && row[position + 1] == "ok") values.Add(double.Parse(row[position], CultureInfo.InvariantCulture));
        }
        return result;
    }

    internal static JsonObject? PairedTimings(string[] baselinePaths, string[] candidatePaths)
    {
        var baseline = new Dictionary<int, List<double>>();
        var candidate = new Dictionary<int, List<double>>();
        var baselineRuns = new List<Dictionary<int, double>>();
        foreach (var (paths, into) in new[] { (baselinePaths, baseline), (candidatePaths, candidate) })
            foreach (var path in paths)
            {
                var samples = LoadTimings(path);
                if (ReferenceEquals(into, baseline)) baselineRuns.Add(samples.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => Median(pair.Value)));
                foreach (var (record, values) in samples)
                {
                    if (!into.TryGetValue(record, out var existing)) into[record] = existing = [];
                    existing.AddRange(values);
                }
            }
        var deltas = new List<double>();
        var regressed = 0;
        var improved = 0;
        foreach (var record in baseline.Keys.Order())
        {
            if (baseline[record].Count == 0 || !candidate.TryGetValue(record, out var values) || values.Count == 0) continue;
            var delta = Delta(Median(baseline[record]), Median(values));
            deltas.Add(delta);
            var own = baselineRuns.Where(run => run.ContainsKey(record)).Select(run => run[record]).ToArray();
            var threshold = Math.Max(5, 2 * (Spread(own) ?? 0));
            if (delta > threshold) regressed++;
            if (delta < -threshold) improved++;
        }
        if (deltas.Count == 0) return null;
        var random = new CorpusBootstrapRandom(20260825);
        var resampled = new double[2000];
        for (var index = 0; index < resampled.Length; index++) resampled[index] = Median(Enumerable.Range(0, deltas.Count).Select(_ => deltas[(int)(random.NextDouble() * deltas.Count)]));
        System.Array.Sort(resampled);
        return new JsonObject { ["records"] = deltas.Count, ["median_delta"] = Median(deltas), ["mean_delta"] = deltas.Average(), ["ci_low"] = resampled[50], ["ci_high"] = resampled[1950], ["regressed"] = regressed, ["improved"] = improved };
    }

    private sealed class Arm
    {
        internal readonly List<JsonObject> Summaries = [];
        internal readonly List<double> Cpu = [];
    }
    private sealed class Corpus
    {
        internal readonly Dictionary<string, Dictionary<string, Arm>> Cells = [];
        internal readonly Dictionary<string, List<string>> Timings = [];
        internal readonly Dictionary<string, List<JsonObject>> Meta = [];
        internal readonly List<JsonObject> Parity = [];
    }
    private static Dictionary<string, Corpus> Collect(string root, string baseline, string candidate)
    {
        var result = new Dictionary<string, Corpus>();
        var directory = Path.Combine(root, "corpus");
        if (!Directory.Exists(directory)) return result;
        foreach (var corpusDirectory in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        foreach (var labelDirectory in Directory.GetDirectories(corpusDirectory).Order(StringComparer.Ordinal))
        {
            var family = Family(Path.GetFileName(labelDirectory));
            var arm = family == candidate ? "PR" : family == baseline ? "master" : null;
            if (arm is null) continue;
            var name = Path.GetFileName(corpusDirectory);
            if (!result.TryGetValue(name, out var corpus)) result[name] = corpus = new Corpus();
            foreach (var cell in Directory.GetDirectories(labelDirectory).Order(StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(cell, "summary.json"))) continue;
                var slot = Family(Path.GetFileName(cell));
                if (!corpus.Cells.TryGetValue(slot, out var arms)) corpus.Cells[slot] = arms = [];
                if (!arms.TryGetValue(arm, out var entry)) arms[arm] = entry = new Arm();
                entry.Summaries.Add(Read(Path.Combine(cell, "summary.json"))["metrics"]!.AsObject());
                if (File.Exists(Path.Combine(cell, "resources.json")) && Read(Path.Combine(cell, "resources.json"))["cpu_ms_per_request"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
                    entry.Cpu.Add(Numeric(value));
            }
            var timings = Path.Combine(labelDirectory, "timings.csv");
            if (File.Exists(timings))
            { if (!corpus.Timings.TryGetValue(arm, out var paths)) corpus.Timings[arm] = paths = []; paths.Add(timings); }
            var meta = Path.Combine(labelDirectory, "timings.meta.json");
            if (File.Exists(meta))
            { if (!corpus.Meta.TryGetValue(arm, out var documents)) corpus.Meta[arm] = documents = []; documents.Add(Read(meta)); }
            if (arm == "PR" && File.Exists(Path.Combine(labelDirectory, "parity.json"))) corpus.Parity.Add(Read(Path.Combine(labelDirectory, "parity.json")));
        }
        return result;
    }

    private static void RenderCells(List<string> lines, string slot, Dictionary<string, Arm> cell, double floor, bool control)
    {
        if (!cell.TryGetValue("master", out var baseline) || !cell.TryGetValue("PR", out var candidate))
        {
            lines.Add($"@ `{slot}` rps: missing a client, cannot compare.");
            return;
        }
        double Mean(Arm arm, string metric, string field) => arm.Summaries.Average(summary => Numeric(summary[metric]!["values"]![field]));
        var baselineRequests = (int)Mean(baseline, "http_reqs", "count");
        var candidateRequests = (int)Mean(candidate, "http_reqs", "count");
        lines.AddRange([$"@ `{slot}` rps · n={baseline.Summaries.Count}/{candidate.Summaries.Count} runs · {baselineRequests}/{candidateRequests} requests/run", "",
            "| metric | master | PR | delta | A/A spread |", "|---|---|---|---|---|"]);
        void Row(string name, double[] before, double[] after, string unit)
        {
            var spread = control ? Spread(before) : null;
            var threshold = spread.HasValue ? Math.Max(2 * spread.Value, 1) : floor;
            var delta = Delta(before.Average(), after.Average());
            lines.Add($"| {name} | {Fixed(before.Average())}{unit} | {Fixed(after.Average())}{unit} | {Arrow(delta, threshold)} {Signed(delta)}% | {(spread.HasValue ? Fixed(spread.Value, 1) + "%" : "n/a")} |");
        }
        if (baseline.Cpu.Count > 0 && candidate.Cpu.Count > 0) Row("CPU-ms/request", [.. baseline.Cpu], [.. candidate.Cpu], "");
        foreach (var (key, name) in CommentMetrics)
            Row(name, baseline.Summaries.Select(summary => Numeric(summary["http_req_duration"]!["values"]![key])).ToArray(),
                candidate.Summaries.Select(summary => Numeric(summary["http_req_duration"]!["values"]![key])).ToArray(), " ms");
        lines.AddRange(["", $"Failure rate — master {Fixed(Mean(baseline, "http_req_failed", "rate") * 100)}%, PR {Fixed(Mean(candidate, "http_req_failed", "rate") * 100)}%."]);
        var dropped = baseline.Summaries.Concat(candidate.Summaries).Sum(summary => (int)Numeric(summary["dropped_iterations"]!["values"]!["count"]));
        if (dropped != 0 || Math.Abs(baselineRequests - candidateRequests) > .01 * Math.Max(baselineRequests, candidateRequests))
            lines.Add($"⚠️ Unequal load — master {baselineRequests} vs PR {candidateRequests} requests/run, {dropped} iteration(s) dropped by k6: the arms did not see the same sample, so the deltas above are not like for like.");
        lines.Add("");
        var baseClasses = baseline.Summaries.Where(summary => summary.ContainsKey("classes")).Select(summary => summary["classes"]!.AsObject()).ToArray();
        var candidateClasses = candidate.Summaries.Where(summary => summary.ContainsKey("classes")).Select(summary => summary["classes"]!.AsObject()).ToArray();
        if (baseClasses.Length == 0 || candidateClasses.Length == 0) return;
        lines.AddRange(["| selector class | requests | master p50 | PR p50 | Δ p50 | master p99 | PR p99 | Δ p99 |", "|---|---|---|---|---|---|---|---|"]);
        foreach (var name in baseClasses[0].Select(pair => pair.Key).Intersect(candidateClasses[0].Select(pair => pair.Key)).OrderBy(name => int.Parse(name.Split('_')[1], CultureInfo.InvariantCulture)))
        {
            double Value(JsonObject[] sets, string field) => sets.Where(set => set.ContainsKey(name)).Average(set => Numeric(set[name]!["values"]![field]));
            var p50 = Delta(Value(baseClasses, "med"), Value(candidateClasses, "med"));
            var p99 = Delta(Value(baseClasses, "p(99)"), Value(candidateClasses, "p(99)"));
            lines.Add($"| {name} | {(int)Value(candidateClasses, "count")} | {Fixed(Value(baseClasses, "med"))} | {Fixed(Value(candidateClasses, "med"))} | {Arrow(p50, floor)} {Signed(p50)}% | {Fixed(Value(baseClasses, "p(99)"))} | {Fixed(Value(candidateClasses, "p(99)"))} | {Arrow(p99, floor)} {Signed(p99)}% |");
        }
        lines.Add("");
    }

    private static void RenderTimings(List<string> lines, Corpus corpus, double floor)
    {
        if (!corpus.Timings.TryGetValue("master", out var baseline) || !corpus.Timings.TryGetValue("PR", out var candidate)) return;
        var paired = PairedTimings([.. baseline], [.. candidate]);
        if (paired is null) return;
        double Value(string key) => Numeric(paired[key]);
        lines.Add($"Paired per-record replay ({paired["records"]} records, medians across passes/runs; {floor}% arrow floor): median delta {Arrow(Value("median_delta"), floor)} {Signed(Value("median_delta"))}% (95% CI {Signed(Value("ci_low"))}% .. {Signed(Value("ci_high"))}%), mean {Signed(Value("mean_delta"))}%; {paired["regressed"]} records slower and {paired["improved"]} faster beyond 5% and twice their own A/A spread.");
        var closed = corpus.Meta.ToDictionary(pair => pair.Key, pair => pair.Value.Where(meta => Numeric(meta["target_rps"]) == 0).Select(meta => Numeric(meta["achieved_rps"])).ToArray());
        if (closed.TryGetValue("master", out var before) && before.Length > 0 && closed.TryGetValue("PR", out var after) && after.Length > 0)
        {
            var delta = Delta(before.Average(), after.Average());
            var levels = corpus.Meta.Values.SelectMany(values => values).Select(meta => (int)Numeric(meta["concurrency"])).Distinct().Order().ToArray();
            var where = levels.Length == 1 ? $"concurrency {levels[0]}" : $"mixed concurrency {string.Join('/', levels)}, not comparable";
            lines.Add($"Closed-loop throughput ({where}): master {Fixed(before.Average(), 1)} req/s, PR {Fixed(after.Average(), 1)} req/s, {Arrow(-delta, floor)} {Signed(delta)}%.");
        }
        var failed = corpus.Meta.Values.SelectMany(values => values).Sum(meta => meta["outcomes"]!.AsObject().Where(pair => pair.Key != "ok").Sum(pair => Numeric(pair.Value)));
        if (failed != 0) lines.Add($"⚠️ {failed} replay request(s) did not return a result — the paired figures above are not clean.");
        lines.Add("");
    }

    internal static string Comment(string root, string baseline, string candidate, bool cached = false)
    {
        var data = Collect(root, baseline, candidate);
        if (!data.Values.Any(corpus => corpus.Cells.Count > 0 || corpus.Timings.Count > 0)) return "No corpus cells were produced, so there is nothing to compare.";
        var lines = new List<string> { "### `eth_call` corpus — PR vs master", "" };
        foreach (var (name, corpus) in data.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            lines.AddRange([$"**`{name}`**", ""]);
            foreach (var (slot, cell) in corpus.Cells.OrderBy(pair => int.TryParse(pair.Key.Split('_')[0], out var rate) ? rate : 0).ThenBy(pair => pair.Key, StringComparer.Ordinal))
                RenderCells(lines, slot, cell, cached ? 5 : 2.5, !cached);
            RenderTimings(lines, corpus, cached ? 5 : 1);
            if (corpus.Parity.Count == 0) lines.Add("Response parity: not checked in this run (no baseline responses to compare against).");
            foreach (var report in corpus.Parity)
            {
                var agree = Numeric(report["matched"]) + (report["both_rpc_errors"] is null ? 0 : Numeric(report["both_rpc_errors"]));
                var who = report["candidate_client"]?.GetValue<string>() ?? "PR";
                var defects = string.Join(", ", report.OrderBy(pair => pair.Key, StringComparer.Ordinal).Where(pair => IsInteger(pair.Value) && Numeric(pair.Value) != 0 && pair.Key is not ("total" or "matched" or "both_rpc_errors")).Select(pair => $"{pair.Key}={pair.Value}"));
                lines.Add($"Response parity ({who}): {agree}/{report["total"]} " + (agree == Numeric(report["total"]) ? "identical to master." : $"**DIVERGES from master** — {defects}"));
            }
            lines.Add("");
        }
        var control = cached ? "No A/A control ran, so a delta within 5% is noise. " : "A/A spread is master against its own repeat: a delta within twice it (min 1%; ~2.5% when no repeat ran) is noise. ";
        lines.Add("<sub>Fixed corpus, seeded request sequence, arms interleaved. " + control + "A PR that changes results is a correctness regression regardless of latency.</sub>");
        if (cached) lines.AddRange(["", "<sub>⚠️ master was **not co-run** here: its numbers come from the cached master baseline, so this run carries no A/A control and the drift between the two jobs is measured nowhere. The no-repeat floor above is therefore 5%, not the in-run 2.5%; for a delta near it, re-run with `baseline_image=<master image>` (both arms in one job) and `rounds=2`.</sub>"]);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^(http_req_duration|http_reqs)\{(.*)\}$")]
    private static partial Regex TaggedMetricRegex();
    [GeneratedRegex("req_name:['\"]?(class_[0-9]+)['\"]?")]
    private static partial Regex ClassTagRegex();
    [GeneratedRegex(@"\Aclass_[0-9]+\z")]
    private static partial Regex ClassNameRegex();
    [GeneratedRegex(@"\A0x[0-9a-f]{64}\z")]
    private static partial Regex BlockHashRegex();
    [GeneratedRegex(@"\A(?<prefix>iso\|[A-Za-z0-9._-]+\|[A-Za-z0-9._-]+\|[A-Za-z0-9._-]+|mix\|[A-Za-z0-9._-]+\|[A-Za-z0-9._-]+)=(?<path>.+jsonbench-summary\.md)\z")]
    private static partial Regex ManifestRegex();
    [GeneratedRegex(@"_r[0-9]+$")]
    private static partial Regex RoundSuffixRegex();
    [GeneratedRegex(@"\A(ok|transport_failure|invalid_response|rpc_error)(:-?\d+)?\z")]
    private static partial Regex StatusRegex();
}

// Keep the seeded bootstrap samples compatible with Python's MT19937 random.choices.
internal sealed class CorpusBootstrapRandom
{
    private readonly uint[] _state = new uint[624];
    private int _index = 624;
    internal CorpusBootstrapRandom(uint seed)
    {
        _state[0] = 19650218;
        for (var i = 1; i < 624; i++) _state[i] = unchecked(1812433253 * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i);
        var position = 1;
        for (var count = 0; count < 624; count++)
        {
            _state[position] = unchecked((_state[position] ^ ((_state[position - 1] ^ (_state[position - 1] >> 30)) * 1664525)) + seed);
            if (++position == 624) { _state[0] = _state[623]; position = 1; }
        }
        for (var count = 0; count < 623; count++)
        {
            _state[position] = unchecked((_state[position] ^ ((_state[position - 1] ^ (_state[position - 1] >> 30)) * 1566083941)) - (uint)position);
            if (++position == 624) { _state[0] = _state[623]; position = 1; }
        }
        _state[0] = 0x80000000;
    }
    private uint Next()
    {
        if (_index == 624)
        {
            for (var i = 0; i < 624; i++)
            {
                var combined = (_state[i] & 0x80000000) | (_state[(i + 1) % 624] & 0x7fffffff);
                _state[i] = _state[(i + 397) % 624] ^ (combined >> 1) ^ ((combined & 1) == 0 ? 0 : 0x9908b0df);
            }
            _index = 0;
        }
        var value = _state[_index++];
        value ^= value >> 11;
        value ^= (value << 7) & 0x9d2c5680;
        value ^= (value << 15) & 0xefc60000;
        return value ^ (value >> 18);
    }
    internal double NextDouble() => ((Next() >> 5) * 67108864d + (Next() >> 6)) / 9007199254740992d;
}
