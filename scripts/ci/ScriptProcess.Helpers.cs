// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;

static class ScriptProcess
{
    internal static async Task<(int Code, string Output, string Error)> Run(string executable, string[] arguments, CancellationToken cancellationToken, bool echo = false, string? workingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = Drain(process.StandardOutput, echo ? Console.Out : null, cancellationToken);
        var stderr = Drain(process.StandardError, echo ? Console.Error : null, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            return (process.ExitCode, await stdout, await stderr);
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
    }

    private static async Task<string> Drain(StreamReader reader, TextWriter? writer, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer, cancellationToken) is var count && count > 0)
        {
            if (writer is not null) await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}
