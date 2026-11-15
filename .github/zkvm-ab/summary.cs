#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false

// Usage: summary.cs <base results dir> <head results dir> <markdown out> <json out>
// Reads the rows that measure.sh wrote for both arms and renders head against base per guest and
// format. Exits with 1 when a block is missing, a run failed or the arms disagree on an output.
// Each directory holds one artifact per guest and format (zkab-base-<guest>-<format>,
// zkab-results-<guest>-<format>). A row counts only in the artifact of its own arm, guest and
// format, so the head code cannot write base rows or the rows of another leg.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: summary.cs <base results dir> <head results dir> <markdown out> <json out>");
    return 2;
}

var units = new Dictionary<string, string> { ["zisk"] = "steps", ["sp1"] = "cycles", ["openvm"] = "instret", ["airbender"] = "cycles" };
var guests = Env("GUESTS").Split(',').Where(units.ContainsKey).ToArray();
var formats = Env("FORMATS").Split(',').Where(format => format.Length > 0).ToArray();

var rows = new Dictionary<(string Vm, string Format), Dictionary<(string Arm, string Block), Row>>();
foreach (var (arm, directory, prefix) in new[] { ("base", args[0], "zkab-base"), ("head", args[1], "zkab-results") })
{
    foreach (var vm in guests)
    {
        foreach (var format in formats)
        {
            var leg = Path.Combine(directory, $"{prefix}-{vm}-{format}");
            if (!Directory.Exists(leg))
                continue;
            var files = Directory.GetFiles(leg, "*.tsv").Where(path => !Path.GetFileName(path).StartsWith('.')).Order(StringComparer.Ordinal);
            foreach (var path in files)
            {
                foreach (var line in File.ReadLines(path))
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 8 || parts[0] != vm || parts[1] != format || parts[2] != arm)
                        continue;
                    if (!rows.TryGetValue((vm, format), out var data))
                        rows[(vm, format)] = data = [];
                    data[(arm, parts[3])] = new Row(parts[4], parts[5], parts[6], parts[7] == "OK");
                }
            }
        }
    }
}

var config = new[] { "BFLAT_IMAGE", "BFLAT_RISCV32_IMAGE", "EXTLIB", "CODEGENOPT" }.Select(name => (Name: name.ToLowerInvariant(), Value: Env(name))).ToArray();
var candidateArm = Env("CANDIDATE_ARM") is { Length: > 0 } candidate ? candidate : "both";
var report = new JsonObject
{
    ["base"] = new JsonObject { ["ref"] = Env("BASE_REF"), ["sha"] = Env("BASE_SHA") },
    ["head"] = new JsonObject { ["ref"] = Env("HEAD_REF"), ["sha"] = Env("HEAD_SHA") },
    ["config"] = new JsonObject(config.Select(entry => KeyValuePair.Create(entry.Name, (JsonNode?)entry.Value))),
    ["candidate_arm"] = candidateArm,
};
var results = new JsonArray();
report["results"] = results;
var lines = new List<string>
{
    "## zkVM guest A/B",
    "",
    $"head `{Env("HEAD_REF")}` ({Short(Env("HEAD_SHA"))}) against base `{Env("BASE_REF")}` ({Short(Env("BASE_SHA"))}). " +
    "Negative deltas mean the head does less work.",
};
if (config.Any(entry => entry.Value.Length > 0))
    lines.AddRange(["", $"Candidate settings ({(candidateArm == "head" ? "head arm only" : "both arms")}):"]);
foreach (var (name, value) in config)
{
    if (value.Length > 0)
        lines.Add($"- {name}: `{value}`");
}

var ok = true;
var verdicts = new List<string>();
foreach (var vm in guests)
{
    var unit = units[vm];
    foreach (var format in formats)
    {
        var data = rows.GetValueOrDefault((vm, format)) ?? [];
        var blocks = data.Keys.Select(key => key.Block).Distinct().OrderBy(block => block.Length).ThenBy(block => block, StringComparer.Ordinal).ToArray();
        var hasCost = data.Values.Any(entry => Num(entry.Cost) is not null);
        long baseTotal = 0, headTotal = 0, baseCosts = 0, headCosts = 0;
        var complete = blocks.Length > 0;
        var costComplete = blocks.Length > 0 && hasCost;
        var identical = blocks.Length > 0;
        var blockResults = new JsonArray();
        lines.AddRange(["", $"### {vm} {format}", ""]);
        var header = $"| block | base {unit} | head {unit} | delta |";
        var rule = "|---|---:|---:|---:|";
        if (hasCost)
        {
            header += " base cost | head cost | cost delta |";
            rule += "---:|---:|---:|";
        }
        lines.AddRange([header + " outputs |", rule + "---|"]);
        foreach (var block in blocks)
        {
            var baseEntry = data.GetValueOrDefault(("base", block));
            var headEntry = data.GetValueOrDefault(("head", block));
            var baseMetric = Num(baseEntry?.Metric);
            var headMetric = Num(headEntry?.Metric);
            var baseCost = Num(baseEntry?.Cost);
            var headCost = Num(headEntry?.Cost);
            var same = baseEntry is { Ok: true } && headEntry is { Ok: true } && baseEntry.Output == headEntry.Output;
            complete &= baseMetric is not null && headMetric is not null;
            costComplete &= baseCost is not null && headCost is not null;
            baseTotal += baseMetric ?? 0;
            headTotal += headMetric ?? 0;
            baseCosts += baseCost ?? 0;
            headCosts += headCost ?? 0;
            identical &= same;
            var row = $"| {(block.EndsWith(".ssz", StringComparison.Ordinal) ? block[..^4] : block)} | {Show(baseMetric)} | {Show(headMetric)} | {Cell(headMetric, baseMetric)} |";
            if (hasCost)
                row += $" {Show(baseCost)} | {Show(headCost)} | {Cell(headCost, baseCost)} |";
            lines.Add(row + $" {(same ? "identical" : "DIFFERENT OR FAILED")} |");
            blockResults.Add(new JsonObject
            {
                ["block"] = block,
                ["base"] = baseMetric,
                ["head"] = headMetric,
                ["change"] = Change(headMetric, baseMetric),
                ["base_cost"] = baseCost,
                ["head_cost"] = headCost,
                ["cost_change"] = Change(headCost, baseCost),
                ["identical"] = same,
            });
        }
        long? totalBase = complete ? baseTotal : null, totalHead = complete ? headTotal : null;
        long? costBase = costComplete ? baseCosts : null, costHead = costComplete ? headCosts : null;
        var totalRow = $"| **total** | {Show(totalBase)} | {Show(totalHead)} | {Cell(totalHead, totalBase)} |";
        if (hasCost)
            totalRow += $" {Show(costBase)} | {Show(costHead)} | {Cell(costHead, costBase)} |";
        lines.Add(totalRow + $" {(identical ? "identical" : "CHECK")} |");
        results.Add(new JsonObject
        {
            ["guest"] = vm,
            ["format"] = format,
            ["unit"] = unit,
            ["blocks"] = blockResults,
            ["total"] = new JsonObject { ["base"] = totalBase, ["head"] = totalHead, ["change"] = Change(totalHead, totalBase) },
            ["total_cost"] = hasCost ? new JsonObject { ["base"] = costBase, ["head"] = costHead, ["change"] = Change(costHead, costBase) } : null,
            ["complete"] = complete,
            ["identical"] = identical,
        });
        verdicts.Add($"- {vm} {format}: {blocks.Length} blocks, {(identical && complete ? "all runs OK, outputs identical" : "a run failed, a block is missing or the outputs differ")}");
        ok = ok && identical && complete;
    }
}

lines.AddRange(["", "### Output check", ""]);
lines.AddRange(verdicts);
report["ok"] = ok;
var markdown = string.Join('\n', lines) + "\n";
Console.WriteLine(markdown);
File.WriteAllText(args[2], markdown);
File.WriteAllText(args[3], report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
return ok ? 0 : 1;

static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

static long? Num(string? value) => value is { Length: > 0 } && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

static double? Percent(long? head, long? baseValue) =>
    head is null || baseValue is null or 0 ? null : Math.Round((double)((head.Value - baseValue.Value) * 100) / baseValue.Value, 4, MidpointRounding.ToEven);

static JsonObject? Change(long? head, long? baseValue) =>
    Percent(head, baseValue) is { } percent ? new JsonObject { ["delta"] = head - baseValue, ["percent"] = percent } : null;

static string Cell(long? head, long? baseValue)
{
    if (Percent(head, baseValue) is not { } value)
        return "n/a";
    var delta = head!.Value - baseValue!.Value;
    var percent = value.ToString("F3", CultureInfo.InvariantCulture);
    return $"{(delta >= 0 ? "+" : "")}{delta.ToString("N0", CultureInfo.InvariantCulture)} ({(percent.StartsWith('-') ? "" : "+")}{percent}%)";
}

static string Show(long? value) => value is null ? "n/a" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

static string Short(string sha) => sha.Length > 0 ? sha[..Math.Min(12, sha.Length)] : "?";

record Row(string Metric, string Cost, string Output, bool Ok);
