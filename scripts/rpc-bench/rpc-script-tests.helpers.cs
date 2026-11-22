// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

internal sealed class RpcScriptTests : IDisposable
{
    internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("rpc-script-tests-").FullName;
    private int _passed;

    internal string Write(string name, string text)
    {
        var path = Path.Combine(DirectoryPath, name);
        File.WriteAllText(path, text);
        return path;
    }

    internal void Case(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static (int Code, string Output, string Error) Run(string script, params string[] arguments)
    {
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Run from inside the repository");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root.FullName };
        foreach (var argument in new[] { "run", "--file", Path.Combine(root.FullName, script), "--" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start script");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(script);
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    internal void Complete() => Console.WriteLine($"Passed {_passed} tests.");

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
