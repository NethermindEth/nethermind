#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include RpcJson.Helpers.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: DeepCheckCompare.cs <label>=<file.jsonl> ...");
    return 2;
}
var clients = new Dictionary<string, Dictionary<string, (string? Method, JsonElement Response)>>();
foreach (var argument in args)
{
    var pair = argument.Split('=', 2);
    if (pair.Length != 2)
    {
        Console.Error.WriteLine($"bad arg (expected label=path): {argument}");
        return 2;
    }
    var rows = new Dictionary<string, (string? Method, JsonElement Response)>();
    foreach (var line in File.ReadLines(pair[1]))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        rows[root.GetProperty("fp").GetString()!] = (root.TryGetProperty("method", out var method) ? method.GetString() : null, root.TryGetProperty("response", out var response) ? response.Clone() : default);
    }
    clients[pair[0]] = rows;
}
var order = clients.Values.SelectMany(rows => rows.Keys).Distinct(StringComparer.Ordinal).ToArray();
var divergent = new List<(string Fingerprint, string? Method, Dictionary<string, (string Kind, object? Detail)> Classes)>();
var malformed = 0;
foreach (var fingerprint in order)
{
    var classes = new Dictionary<string, (string Kind, object? Detail)>();
    string? method = null;
    foreach (var (label, rows) in clients)
    {
        if (!rows.TryGetValue(fingerprint, out var row)) classes[label] = ("missing", null);
        else { method = row.Method; classes[label] = Classify(row.Response); }
    }
    if (classes.Values.Any(value => value.Kind == "malformed")) malformed++;
    if (classes.Values.Distinct().Skip(1).Any()) divergent.Add((fingerprint, method, classes));
}
Console.WriteLine($"clients:            [{string.Join(", ", clients.Keys.Select(label => "'" + label.Replace("'", "\\'", StringComparison.Ordinal) + "'"))}]");
Console.WriteLine($"requests compared:  {order.Length}");
Console.WriteLine($"DIVERGENT (disagree): {divergent.Count}");
Console.WriteLine($"MALFORMED (any client): {malformed}");
if (divergent.Count > 0)
{
    Console.WriteLine("\n-- divergences (first 60) --");
    foreach (var row in divergent.Take(60))
        Console.WriteLine($"  {row.Method ?? "None"} [{row.Fingerprint}]: {string.Join("  ", row.Classes.Select(pair => pair.Key + "=" + pair.Value.Kind + (pair.Value.Detail is { } detail ? "#" + detail : "")))}");
}
return divergent.Count > 0 || malformed > 0 ? 1 : 0;

static (string Kind, object? Detail) Classify(JsonElement response)
{
    if (response.ValueKind != JsonValueKind.Object) return ("malformed", null);
    if (response.TryGetProperty("_capture_error", out var capture))
    {
        var text = capture.ValueKind == JsonValueKind.String ? capture.GetString()! : capture.ToString();
        return ("malformed", text[..Math.Min(60, text.Length)]);
    }
    if (response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        return ("error", error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind != JsonValueKind.Null ? code.ValueKind switch { JsonValueKind.String => code.GetString(), JsonValueKind.Number => code.GetDecimal(), JsonValueKind.True => true, JsonValueKind.False => false, _ => code.GetRawText() } : null);
    if (response.TryGetProperty("result", out var result))
        return ("result", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RpcJson.Canonical(result))))[..12]);
    return ("malformed", null);
}
