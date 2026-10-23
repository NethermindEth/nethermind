#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include PruneRuntimes.Helpers.cs

var temporary = Directory.CreateTempSubdirectory("runtime prune tests ");
var count = 0;
try
{
    foreach (var rid in new[] { "linux-x64", "linux-arm64", "osx-arm64", "win-x64" })
    {
        var root = Path.Combine(temporary.FullName, rid);
        var names = new[] { "linux", "unix", "win", "osx", "linux-x64", "linux-arm64", "osx-arm64", "win-x64", "linux-musl-x64" };
        foreach (var project in new[] { "project one", "project two" })
            foreach (var name in names)
            {
                var directory = Path.Combine(root, project, "runtimes", name, "native");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "asset"), name);
            }
        Directory.CreateDirectory(Path.Combine(root, "unrelated-arch"));
        using var output = new StringWriter();
        if (await RuntimePruner.Run([rid, root], output, Console.Error) != 0)
            throw new Exception("Pruning failed.");
        foreach (var project in new[] { "project one", "project two" })
            foreach (var name in names)
                if (Directory.Exists(Path.Combine(root, project, "runtimes", name)) != (!name.Contains('-') || name == rid))
                    throw new Exception($"Incorrect retained runtime: {project}/{name}");
        if (!Directory.Exists(Path.Combine(root, "unrelated-arch")) || output.ToString().Contains("::warning::"))
            throw new Exception("Unrelated directory removed or retained RID not found.");
        Console.WriteLine($"PASS: preserve {rid} and managed fallbacks");
        count++;
    }

    using var missing = new StringWriter();
    if (await RuntimePruner.Run(["linux-x64", Path.Combine(temporary.FullName, "missing")], missing, Console.Error) != 1 || !missing.ToString().Contains("::error::"))
        throw new Exception("Missing root must fail.");
    Console.WriteLine("PASS: missing bin root fails");
    count++;

    var empty = Directory.CreateDirectory(Path.Combine(temporary.FullName, "empty"));
    using var warning = new StringWriter();
    if (await RuntimePruner.Run(["linux-x64", empty.FullName], warning, Console.Error) != 0 || !warning.ToString().Contains("::warning::no runtimes/linux-x64"))
        throw new Exception("No native dependencies must warn and succeed.");
    Console.WriteLine("PASS: no native dependencies warns without failing");
    count++;

    if (!OperatingSystem.IsWindows())
    {
        var outside = Directory.CreateDirectory(Path.Combine(temporary.FullName, "outside", "runtimes", "foreign-arch"));
        File.WriteAllText(Path.Combine(outside.FullName, "asset"), "preserve");
        Directory.CreateSymbolicLink(Path.Combine(empty.FullName, "linked"), Path.Combine(temporary.FullName, "outside"));
        Directory.CreateDirectory(Path.Combine(empty.FullName, "runtimes"));
        Directory.CreateSymbolicLink(Path.Combine(empty.FullName, "runtimes", "foreign-arch"), outside.FullName);
        var removable = Directory.CreateDirectory(Path.Combine(empty.FullName, "runtimes", "other-arch"));
        Directory.CreateSymbolicLink(Path.Combine(removable.FullName, "external"), outside.FullName);
        if (await RuntimePruner.Run(["linux-x64", empty.FullName], TextWriter.Null, Console.Error) != 0 ||
            !File.Exists(Path.Combine(outside.FullName, "asset")) ||
            !Directory.Exists(Path.Combine(empty.FullName, "runtimes", "foreign-arch")) ||
            Directory.Exists(removable.FullName))
            throw new Exception("Pruning must not follow directory links outside the bin root.");
        Console.WriteLine("PASS: directory links cannot escape the bin root");
        count++;
    }
    Console.WriteLine($"Passed {count} tests.");
}
finally
{
    temporary.Delete(recursive: true);
}
