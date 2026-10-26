// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

internal static class BenchmarkProcess
{
    internal static string EnvironmentValue(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
    internal static void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} | {message}");

    internal static async Task<(int Code, string Output, string Error)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException exception) { Console.Error.WriteLine($"Child exited during cleanup: {exception.Message}"); }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // Observe both drain tasks even when cancellation interrupts the child.
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Console.Error.WriteLine("Process output draining cancelled."); }
        }
    }

    internal static async Task<(int Code, string Output, string Error)> RootAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        var identity = await RunAsync("id", ["-u"], cancellationToken);
        return identity.Code == 0 && identity.Output.Trim() == "0"
            ? await RunAsync(executable, arguments, cancellationToken, input)
            : await RunAsync("sudo", new[] { executable }.Concat(arguments), cancellationToken, input);
    }
}
