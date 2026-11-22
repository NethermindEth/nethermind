#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false
#:include ../../scripts/ci/script-process.helpers.cs

using System.Text.RegularExpressions;

var vm = args[0];
var format = args[1];
var arm = args[2];
var guest = Path.GetFullPath(args[3]);
var inputs = Path.GetFullPath(args[4]);
var output = args[5];
var logPath = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")!, $"run-{vm}-{format}-{arm}.log");
var bin = Path.Combine(guest, "bin");
var elf = format == "Trusted" ? "nethermind-trusted" : "nethermind";
var rows = File.ReadLines(Path.Combine(inputs, "expected.tsv"))
    .Select(line => line.Split('\t'))
    .Where(row => row[0] == format)
    .Select(row => (Block: row[1], File: row[2], Output: row[3], Digest: row[4]))
    .ToArray();

await Require("chmod", ["+x", Path.Combine(bin, elf)]);
foreach (var row in rows)
    File.Copy(Path.Combine(inputs, row.File), Path.Combine(bin, row.File), overwrite: true);

if (vm == "openvm")
{
    foreach (var row in rows)
        await Require("make", ["-C", guest, Path.Combine(bin, row.File + ".raw")]);
    var arguments = new List<string> { "run", "--rm", "--mount", $"type=bind,source={bin},target=/n", "zkab-openvm-cost:local", $"/n/{elf}" };
    arguments.AddRange(rows.Select(row => $"/n/{row.File}.raw"));
    var (exit, log) = await Run("docker", arguments.ToArray());
    Console.Write(log);
    var lines = log.Split('\n');
    foreach (var row in rows)
    {
        var line = lines.FirstOrDefault(line => line.StartsWith($"RESULT /n/{row.File}.raw ", StringComparison.Ordinal)) ?? "";
        var metric = Read(line, @" instret=([0-9]+)");
        var cost = Read(line, @" cost=([0-9]+)");
        var got = Read(line, @" public_values=([0-9a-f]+)");
        Emit(row.Block, metric, cost, got, exit == 0 && got == row.Digest);
    }
    return;
}

foreach (var row in rows)
{
    List<string> make =
    [
        "-C", guest,
        vm switch
        {
            "zisk" => "stats",
            "sp1" => "run",
            "airbender" => "verify",
            _ => throw new InvalidOperationException($"unknown guest {vm}")
        },
        $"INPUT={row.File}",
        $"GUEST_INPUT={format}"
    ];
    if (vm == "airbender") make.Add($"EXPECTED_DIGEST={row.Digest}");
    var (exit, log) = await Run("make", make.ToArray());
    var metric = vm switch
    {
        "zisk" => Read(log, @"(?m)^STEPS[ \t]+([0-9][0-9,]*)[ \t]*\r?$").Replace(",", "", StringComparison.Ordinal),
        "sp1" => Read(log, @"(?m)^cycles ([0-9]+)\r?$"),
        _ => Read(log, @"Cycles: ([0-9]+)")
    };
    var cost = vm == "zisk" ? Read(log, @"(?m)^TOTAL[ \t]+([0-9][0-9,]*)(?:[ \t].*)?\r?$").Replace(",", "", StringComparison.Ordinal) : "";
    var got = vm switch
    {
        "zisk" => log.Contains(row.Output, StringComparison.Ordinal) ? row.Output : "",
        "sp1" => Read(log, @"(?m)^output ([0-9a-f]+)\r?$"),
        _ => exit == 0 ? row.Digest : "FAIL"
    };
    var wanted = vm == "airbender" ? row.Digest : row.Output;
    var ok = exit == 0 && got == wanted && !log.Contains("Exception", StringComparison.Ordinal) && !log.Contains("Invalid Block", StringComparison.Ordinal);
    if (!ok) Console.WriteLine(string.Join('\n', log.TrimEnd('\n').Split('\n').TakeLast(40)));
    Emit(row.Block, metric, cost, got, ok);
}

static string Read(string text, string pattern) => Regex.Match(text, pattern).Groups[1].Value;

async Task<(int Exit, string Log)> Run(string program, string[] arguments)
{
    var result = await ScriptProcess.Run(program, arguments, CancellationToken.None);
    var log = result.Output + result.Error;
    await File.WriteAllTextAsync(logPath, log);
    return (result.Code, log);
}

static async Task Require(string program, string[] arguments)
{
    var result = await ScriptProcess.Run(program, arguments, CancellationToken.None);
    Console.Error.Write(result.Error);
    if (result.Code != 0) throw new InvalidOperationException($"{program} failed with exit code {result.Code}");
}

void Emit(string block, string metric, string cost, string actual, bool ok)
{
    var line = string.Join('\t', vm, format, arm, block, ValueOrDash(metric), ValueOrDash(cost), ValueOrDash(actual), ok ? "OK" : "MISMATCH");
    Console.WriteLine(line);
    File.AppendAllText(output, line + "\n");
}

static string ValueOrDash(string value) => value.Length == 0 ? "-" : value;
