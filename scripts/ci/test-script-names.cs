#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ../version.helpers.cs
#:include script-process.helpers.cs

var runner = Path.Combine(ScriptVersion.Root(), "scripts/ci/run-script-tests.cs");
var directory = Directory.CreateTempSubdirectory("script-names-");
var passed = 0;
try
{
    foreach (var name in new[] { "TestExample.cs", "test_example.cs", "test--example.cs", "test-.cs", "Example.Helpers.cs", "example.Helpers.cs", "Example.helpers.cs" })
    {
        var file = Path.Combine(directory.FullName, name);
        File.WriteAllText(file, "Console.WriteLine(1);");
        try
        {
            foreach (var target in new[] { file, directory.FullName })
            {
                var result = await ScriptProcess.Run("dotnet", ["run", "--file", runner, "--", "--check", target], CancellationToken.None);
                if (result.Code != 1 || !result.Error.Contains("lowercase kebab-case", StringComparison.Ordinal) || !result.Error.Contains(file, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Accepted invalid script name {target}: {result.Output}{result.Error}");
                Console.WriteLine($"PASS reject {name} via {(target == file ? "file" : "directory")}");
                passed++;
            }
        }
        finally { File.Delete(file); }
    }

    File.WriteAllText(Path.Combine(directory.FullName, "test-example2.cs"), "#:include example.helpers.cs\nConsole.WriteLine(Example.Value);");
    File.WriteAllText(Path.Combine(directory.FullName, "example.helpers.cs"), "static class Example { internal const int Value = 1; }");
    foreach (var excluded in new[] { "build", "bin", "obj" })
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(directory.FullName, excluded)).FullName, "Invalid.cs"), "not C#");
    var valid = await ScriptProcess.Run("dotnet", ["run", "--file", runner, "--", "--check", directory.FullName], CancellationToken.None);
    if (valid.Code != 0 || !valid.Output.Contains("Validated 1 C# entry point(s)", StringComparison.Ordinal))
        throw new InvalidOperationException($"Valid names, helpers or excluded directories rejected: {valid.Output}{valid.Error}");
    Console.WriteLine("PASS valid names, included helper and excluded directories");
    passed++;
    Console.WriteLine($"Passed {passed} tests.");
}
finally { directory.Delete(recursive: true); }
