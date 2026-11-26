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
    const string commented = "    <!-- <VersionPrefix>2.3.0</VersionPrefix> -->\n";
    const string commentedLines = "    <!--\n    <VersionPrefix>0.0.1</VersionPrefix>\n    -->\n";
    foreach (var (branch, current, expected, success, comment) in new[]
    {
        ("release/1.31.0", "1.31.0", "1.32.0", true, ""),
        ("release/2.0.0", "2.0.0", "2.1.0", true, ""),
        ("release/1.31.0", "1.32.0", "1.32.0", true, ""),
        ("release/1.31.1", "1.31.1", "1.32.0", false, ""),
        ("releases/1.31.0", "1.31.0", "1.32.0", false, ""),
        ("feature/something", "1.31.0", "1.32.0", false, ""),
        ("release/1.31.0", "1.40.0", "1.32.0", true, ""),
        ("release/1.32.0", "1.32.0", "1.33.0", true, ""),
        ("release/1.31.1", "1.31.0", "1.32.0", false, ""),
        ("release/2.3.0", "", "2.4.0", true, ""),
        ("release/2.3.0", (string?)null, "2.4.0", false, ""),
        ("release/2.3.0", null, "2.4.0", false, commented),
        ("release/2.3.0", "2.3.0", "2.4.0", true, commented),
        ("release/2.3.0", "2.3.0", "2.4.0", true, commentedLines)
    })
    {
        var file = Path.Combine(temporary.FullName, "Directory.Build.props");
        string Props(string? value) => $"<Project>\n  <PropertyGroup>\n{comment}{(value is null ? "" : $"    <VersionPrefix>{value}</VersionPrefix>\n")}    <VersionSuffix>preview</VersionSuffix>\n  </PropertyGroup>\n</Project>\n";
        var contents = Props(current);
        File.WriteAllText(file, contents);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await ScriptProcess.Run("dotnet", ["run", "--file", script, "--", $"refs/heads/{branch}", file], timeout.Token);
        var passed = current is null
            ? result.Code == 1 && result.Error == $"error: '{file}' declares no <VersionPrefix>{Environment.NewLine}" &&
              result.Output.Length == 0 && File.ReadAllText(file) == contents
            : (result.Code == 0) == success && File.ReadAllText(file) == (success ? Props(expected) : contents);
        var name = $"{branch}/{(current switch { null => "missing VersionPrefix", "" => "empty VersionPrefix", _ => current })}{(comment.Length == 0 ? "" : comment == commented ? " with commented VersionPrefix" : " with multiline commented VersionPrefix")}";
        if (!passed)
            throw new InvalidOperationException($"{name}: exit {result.Code}\n{result.Output}{result.Error}");
        Console.WriteLine($"PASS: {name}");
        count++;
    }
    Console.WriteLine($"Passed {count} tests.");
}
finally
{
    temporary.Delete(recursive: true);
}
