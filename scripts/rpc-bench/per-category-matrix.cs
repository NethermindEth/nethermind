#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var arguments = args.Length == 1 && args[0].StartsWith('@') ? File.ReadLines(args[0][1..]).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray() : args;
var isolated = new Dictionary<(string Scenario, string Client, string Rps), Dictionary<string, double>>();
var mixed = new Dictionary<(string Client, string Rps), (Dictionary<string, double> Overall, Dictionary<string, double> Methods)>();
var clients = new List<string>();
var rates = new List<string>();
var isolatedScenarios = new List<string>();
var mixedScenarios = new List<string>();
foreach (var argument in arguments)
{
    var split = argument.Split('=', 2);
    var key = split[0].Split('|');
    var parsed = Parse(split[1]);
    string client;
    string rate;
    if (key[0] == "iso")
    {
        client = key[2]; rate = key[3];
        isolated[(key[1], client, rate)] = parsed.Overall;
        Add(isolatedScenarios, key[1]);
    }
    else
    {
        client = key[1]; rate = key[2];
        mixed[(client, rate)] = parsed;
        foreach (var scenario in parsed.Methods.Keys) Add(mixedScenarios, scenario);
    }
    Add(clients, client); Add(rates, rate);
}
rates = rates.OrderBy(rate => int.TryParse(rate.Split('_')[0], out var number) ? number : 0).ThenBy(rate => rate, StringComparer.Ordinal).ToList();
var columns = clients.SelectMany(client => rates.Select(rate => (Client: client, Rate: rate))).ToArray();
Console.WriteLine("## MIXED - overall (client x rps): throughput r/s / checks% / avg / median / p90 / p99 ms\n");
Header("client", rates.Select(rate => $"rps {rate}"));
foreach (var client in clients)
{
    var row = new List<string> { client };
    foreach (var rate in rates)
    {
        if (!mixed.TryGetValue((client, rate), out var cell) || cell.Overall.Count == 0) { row.Add("-"); continue; }
        var values = cell.Overall;
        var checks = values.TryGetValue("checks", out var check) ? $"{check:F0}%" : "na";
        row.Add($"{values.GetValueOrDefault("tput"):F0} / {checks} / {values.GetValueOrDefault("avg"):F2} / {values.GetValueOrDefault("median"):F2} / {values.GetValueOrDefault("p90"):F0} / {values.GetValueOrDefault("p99"):F0}");
    }
    Row(row);
}
if (isolatedScenarios.Count > 0)
{
    Console.WriteLine("\n## ISOLATED - each scenario ALONE at the sweep rps: achieved r/s / p99 ms (achieved << target = that scenario's ceiling)\n");
    Header("scenario", columns.Select(column => $"{column.Client}@{column.Rate}"));
    foreach (var scenario in isolatedScenarios)
        Row(new[] { scenario }.Concat(columns.Select(column => isolated.TryGetValue((scenario, column.Client, column.Rate), out var cell) && cell.TryGetValue("p99", out var p99) && cell.TryGetValue("tput", out var throughput) ? $"{throughput:F0}/{p99:F0}" : "-")));
}
if (mixedScenarios.Count > 0)
{
    Console.WriteLine("\n## MIXED - per-scenario p99 ms (all scenarios at once)\n");
    Header("scenario", columns.Select(column => $"{column.Client}@{column.Rate}"));
    foreach (var scenario in mixedScenarios)
        Row(new[] { scenario }.Concat(columns.Select(column => mixed.TryGetValue((column.Client, column.Rate), out var cell) && cell.Methods.TryGetValue(scenario, out var p99) ? $"{p99:F0}" : "-")));
}

static void Add(List<string> values, string value) { if (!values.Contains(value)) values.Add(value); }
static void Row(IEnumerable<string> cells) => Console.WriteLine("| " + string.Join(" | ", cells) + " |");
static void Header(string first, IEnumerable<string> columns)
{
    var cells = new[] { first }.Concat(columns).ToArray();
    Row(cells);
    Console.WriteLine("|" + string.Concat(Enumerable.Repeat("---|", cells.Length)));
}

static (Dictionary<string, double> Overall, Dictionary<string, double> Methods) Parse(string path)
{
    var text = File.ReadAllText(path);
    var overall = new Dictionary<string, double>();
    var methods = new Dictionary<string, double>();
    foreach (var (key, label) in new[] { ("avg", "latency avg (ms)"), ("median", "latency p50 (ms)"), ("p90", "latency p90 (ms)"), ("p99", "latency p99 (ms)"), ("tput", "throughput (req/s)"), ("checks", "checks passed") })
    {
        var match = OverallRowRegex().Matches(text).FirstOrDefault(match => match.Groups[1].Value == label && (key != "checks" || match.Groups[3].Success));
        if (match is not null) overall[key] = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
    }
    var inMethods = false;
    foreach (var line in text.Split('\n'))
    {
        if (line.Trim().StartsWith("### Per method", StringComparison.Ordinal)) { inMethods = true; continue; }
        if (!inMethods) continue;
        var match = MethodRowRegex().Match(line.Trim());
        if (match.Success && match.Groups[1].Value != "method")
            methods[UnsafeNameRegex().Replace(match.Groups[1].Value, "-")] = double.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
    }
    return (overall, methods);
}

partial class Program
{
    [GeneratedRegex(@"\|\s*(latency (?:avg|p50|p90|p99) \(ms\)|throughput \(req/s\)|checks passed)\s*\|\s*([0-9.]+)(%)?")]
    private static partial Regex OverallRowRegex();
    [GeneratedRegex(@"^\|\s*([\w/#-]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|")]
    private static partial Regex MethodRowRegex();
    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex UnsafeNameRegex();
}
