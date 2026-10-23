#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

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
try
{
    await Check(cancellation.Token, "empty suite", false, "No test suites found", temporary);
    await Check(cancellation.Token, "empty analyzer directory", true, "Validated 0 C#", "--check", temporary);

    var test = Path.Combine(temporary, "TestProbe.cs");
    foreach (var (name, source, success, output) in new[]
    {
        ("C# success with a space-containing path", "Console.WriteLine(\"Passed 1 tests.\");", true, "Passed 1 test file(s)."),
        ("C# failure", "return 7;", false, "exited with code 7"),
        ("C# zero cases", "Console.WriteLine(\"Passed 0 tests.\");", false, "did not report a positive"),
        ("C# no summary", "Console.WriteLine(\"no tests\");", false, "did not report a positive")
    })
    {
        File.WriteAllText(test, source);
        await Check(cancellation.Token, name, success, output, temporary);
    }
    File.Delete(test);

    var pythonTest = Path.Combine(temporary, "test_probe.py");
    foreach (var (name, source, success, output) in new[]
    {
        ("Python success", "import unittest\nclass Probe(unittest.TestCase):\n def test_pass(self): self.assertTrue(True)\n", true, "Ran 1 test"),
        ("Python failure", "import unittest\nclass Probe(unittest.TestCase):\n def test_fail(self): self.fail('expected failure')\n", false, "FAILED"),
        ("Python zero cases", "# no cases\n", false, "Ran 0 tests")
    })
    {
        File.WriteAllText(pythonTest, source);
        await Check(cancellation.Token, name, success, output, temporary);
    }
    File.Delete(pythonTest);

    if (!OperatingSystem.IsWindows())
    {
        var shellTest = Path.Combine(temporary, "test-probe.sh");
        File.WriteAllText(shellTest, "printf 'shell fixture passed\\n'\n");
        await Check(cancellation.Token, "shell discovery", true, "shell fixture passed", temporary);
        File.WriteAllText(test, "Console.WriteLine(\"Passed 1 tests.\");");
        await Check(cancellation.Token, "mixed shell and C# discovery", true, "Passed 2 test file(s).", temporary);
        File.Delete(test);
        File.WriteAllText(shellTest, "exit 7\n");
        await Check(cancellation.Token, "shell failure", false, "exited with code 7", temporary);
        File.Delete(shellTest);
    }

    var helper = Path.Combine(temporary, "Probe.Helpers.cs");
    File.WriteAllText(test, "#:include Probe.Helpers.cs\nConsole.WriteLine(Probe.Value);\n");
    File.WriteAllText(helper, "static class Probe { internal const int Value = 1; }\n");
    File.WriteAllText(Path.Combine(temporary, ".editorconfig"), "root = true\n[*.cs]\ndotnet_diagnostic.IDE0005.severity = warning\n");
    await Check(cancellation.Token, "included helper builds once", true, "Validated 1 C#", "--check", temporary);
    File.WriteAllText(helper, "using System.Text;\nstatic class Probe { internal const int Value = 1; }\n");
    await Check(cancellation.Token, "unused import in included helper", false, "IDE0005", "--check", temporary);
    File.Delete(helper);
    await Check(cancellation.Token, "missing included helper", false, "Probe.Helpers.cs", "--check", temporary);

    Console.WriteLine($"Passed {count} tests.");
    return 0;
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

async Task Check(CancellationToken cancellationToken, string name, bool success, string expectedOutput, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }
    };
    foreach (var argument in new[] { "run", "--file", runner, "--" }.Concat(arguments))
        process.StartInfo.ArgumentList.Add(argument);
    process.StartInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
    process.Start();
    var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
    string output;
    try
    {
        await process.WaitForExitAsync(cancellationToken);
        output = await stdout + await stderr;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdout, stderr);
    }
    if ((process.ExitCode == 0) != success || !output.Contains(expectedOutput, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: unexpected exit {process.ExitCode} or output:\n{output}");
    Console.WriteLine($"PASS: {name}");
    count++;
}
