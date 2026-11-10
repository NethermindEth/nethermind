#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ../version.helpers.cs
#:include script-process.helpers.cs

var script = Path.Combine(ScriptVersion.Root(), "scripts/bump-version.cs");
var temporary = Directory.CreateTempSubdirectory("version bump ");
var count = 0;
try
{
    foreach (var (branch, current, expected, success) in new[]
    {
        ("release/1.31.0", "1.31.0", "1.32.0", true),
        ("release/2.0.0", "2.0.0", "2.1.0", true),
        ("release/1.31.0", "1.32.0", "1.32.0", true),
        ("release/1.31.1", "1.31.1", "1.32.0", false),
        ("releases/1.31.0", "1.31.0", "1.32.0", false),
        ("feature/something", "1.31.0", "1.32.0", false),
        ("release/1.31.0", "1.40.0", "1.32.0", true),
        ("release/1.32.0", "1.32.0", "1.33.0", true),
        ("release/1.31.1", "1.31.0", "1.32.0", false)
    })
    {
        var file = Path.Combine(temporary.FullName, "Directory.Build.props");
        File.WriteAllText(file, $"<Project>\n  <PropertyGroup>\n    <VersionPrefix>{current}</VersionPrefix>\n    <VersionSuffix>preview</VersionSuffix>\n  </PropertyGroup>\n</Project>\n");
        var result = await ScriptProcess.Run("dotnet", ["run", "--file", script, "--", $"refs/heads/{branch}", file], CancellationToken.None);
        if ((result.Code == 0) != success || (success && ScriptVersion.Property(File.ReadAllText(file), "VersionPrefix") != expected))
            throw new InvalidOperationException($"{branch}/{current}: {result.Output}{result.Error}");
        Console.WriteLine($"PASS: {branch}/{current}");
        count++;
    }
    Console.WriteLine($"Passed {count} tests.");
}
finally
{
    temporary.Delete(recursive: true);
}
