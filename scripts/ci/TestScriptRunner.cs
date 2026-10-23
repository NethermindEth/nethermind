#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ScriptProcess.Helpers.cs

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var repository = new DirectoryInfo(Directory.GetCurrentDirectory());
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "ci", "RunScriptTests.cs")))
    repository = repository.Parent;
var runner = Path.Combine(repository?.FullName ?? throw new InvalidOperationException("Run from the repository or one of its subdirectories."), "scripts", "ci", "RunScriptTests.cs");
var temporary = Path.Combine(Path.GetTempPath(), $"nethermind runner tests {Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
var count = 0;
var bytecode = Environment.GetEnvironmentVariable("PYTHONDONTWRITEBYTECODE");
Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", "1");
try
{
    await Check("empty suite", false, "No test suites found", temporary);
    await Check("empty analyzer directory", true, "Validated 0 C#", "--check", temporary);

    var test = Path.Combine(temporary, "TestProbe.cs");
    foreach (var (file, name, source, success, output) in new[]
    {
        ("TestProbe.cs", "C# success with a space-containing path", "Console.WriteLine(\"Passed 1 tests.\");", true, "Passed 1 test file(s)."),
        ("TestProbe.cs", "C# failure", "return 7;", false, "exited with code 7"),
        ("TestProbe.cs", "C# zero cases", "Console.WriteLine(\"Passed 0 tests.\");", false, "did not report a positive"),
        ("TestProbe.cs", "C# no summary", "Console.WriteLine(\"no tests\");", false, "did not report a positive"),
        ("test_probe.py", "Python success", "import unittest\nclass Probe(unittest.TestCase):\n def test_pass(self): self.assertTrue(True)\n", true, "Ran 1 test"),
        ("test_probe.py", "Python failure", "import unittest\nclass Probe(unittest.TestCase):\n def test_fail(self): self.fail('expected failure')\n", false, "FAILED"),
        ("test_probe.py", "Python zero cases", "# no cases\n", false, "Ran 0 tests")
    })
    {
        var path = Path.Combine(temporary, file);
        File.WriteAllText(path, source);
        await Check(name, success, output, temporary);
        File.Delete(path);
    }

    File.WriteAllText(test, "Console.Write(Environment.CurrentDirectory); Console.Error.Write(\"stderr\");");
    var streams = await ScriptProcess.Run("dotnet", ["run", "--file", test], cancellation.Token, workingDirectory: temporary);
    if (streams.Code != 0 || streams.Output != temporary || streams.Error != "stderr")
        throw new InvalidOperationException("Process capture must preserve the working directory and separate streams without adding newlines.");
    Console.WriteLine("PASS: working directory and exact stdout/stderr capture");
    count++;
    File.Delete(test);

    if (!OperatingSystem.IsWindows())
    {
        var shellTest = Path.Combine(temporary, "test-probe.sh");
        File.WriteAllText(shellTest, "printf 'shell fixture passed\\n'\n");
        await Check("shell discovery", true, "shell fixture passed", temporary);
        File.WriteAllText(test, "Console.WriteLine(\"Passed 1 tests.\");");
        await Check("mixed shell and C# discovery", true, "Passed 2 test file(s).", temporary);
        File.Delete(test);
        File.WriteAllText(shellTest, "exit 7\n");
        await Check("shell failure", false, "exited with code 7", temporary);
        File.Delete(shellTest);
    }

    var helper = Path.Combine(temporary, "Probe.Helpers.cs");
    File.WriteAllText(test, "#:include Probe.Helpers.cs\nConsole.WriteLine(Probe.Value);\n");
    File.WriteAllText(helper, "static class Probe { internal const int Value = 1; }\n");
    File.WriteAllText(Path.Combine(temporary, ".editorconfig"), "root = true\n[*.cs]\ndotnet_diagnostic.IDE0005.severity = warning\n");
    await Check("included helper builds once", true, "Validated 1 C#", "--check", temporary);
    File.WriteAllText(helper, "using System.Text;\nstatic class Probe { internal const int Value = 1; }\n");
    await Check("unused import in included helper", false, "IDE0005", "--check", temporary);
    File.Delete(helper);
    await Check("missing included helper", false, "Probe.Helpers.cs", "--check", temporary);

    Console.WriteLine($"Passed {count} tests.");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", bytecode);
    Directory.Delete(temporary, recursive: true);
}

async Task Check(string name, bool success, string expectedOutput, params string[] arguments)
{
    var result = await ScriptProcess.Run("dotnet", ["run", "--file", runner, "--", .. arguments], cancellation.Token);
    var output = result.Output + result.Error;
    if ((result.Code == 0) != success || !output.Contains(expectedOutput, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: unexpected exit {result.Code} or output:\n{output}");
    Console.WriteLine($"PASS: {name}");
    count++;
}
