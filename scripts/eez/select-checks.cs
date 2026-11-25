#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false
#:include checks.helpers.cs
#:include ../ci/script-process.helpers.cs

using System.Text.Json;

var root = Environment.CurrentDirectory;
var projects = EezChecks.Projects(root);
EezChecks.ValidateDependencies(projects);
var full = Environment.GetEnvironmentVariable("EEZ_FULL") == "true";
var paths = Array.Empty<string>();
if (!full)
{
    var before = Environment.GetEnvironmentVariable("BASE_SHA") ?? "";
    var after = Environment.GetEnvironmentVariable("HEAD_SHA") ?? "";
    if (before.Length != 40 || after.Length != 40 || !before.All(char.IsAsciiHexDigit) || !after.All(char.IsAsciiHexDigit))
        throw new InvalidDataException("Expected full base and head commit SHAs.");
    if (before.All(value => value == '0')) full = true;
    else
    {
        var result = await ScriptProcess.Run("git", ["diff", "--name-only", "--no-renames", "-z", before, after], CancellationToken.None);
        if (result.Code != 0) throw new InvalidOperationException(result.Error);
        paths = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
var plan = EezChecks.Select(projects, paths, full);
var output = $"unit={plan.Unit.ToString().ToLowerInvariant()}\nstreaming={plan.Streaming.ToString().ToLowerInvariant()}\nlive={plan.Live}\n";
Console.WriteLine(JsonSerializer.Serialize(plan));
if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { } destination) File.AppendAllText(destination, output);
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { } summary)
    File.AppendAllText(summary, $"EEZ selection: unit={plan.Unit}, streaming={plan.Streaming}, live={plan.Live}.\n");
