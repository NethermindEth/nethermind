#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ZiskBench.Helpers.cs
using System.Text.Json;
using System.Text.Json.Nodes;

try
{
    var options = ZiskBench.Options(args, ["input", "log", "into"], ["input", "log", "into"]);
    if (options is null) return 0;
    var row = ZiskBench.Parse(File.ReadAllText(options["log"]), options["input"]);
    var path = options["into"];
    var rows = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : [];
    var ordered = rows.Where(existing => existing!["input"]!.GetValue<string>() != options["input"]).Append(row)
        .OrderBy(existing => existing!["input"]!.GetValue<string>(), StringComparer.Ordinal);
    File.WriteAllText(path, new JsonArray(ordered.Select(existing => existing!.DeepClone()).ToArray()).ToJsonString(new() { WriteIndented = true }) + "\n");
    Console.WriteLine($"{options["input"]}: {row["steps"]} steps, {row["total"]} cost");
    return 0;
}
catch (FormatException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
