// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>Drives the nethtest binary built next to the tests, the way external tooling runs it.</summary>
internal static class NethtestProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task<(string Stdout, string Stderr)> Run(string[] args, string stdin = null)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nethtest.exe" : "nethtest");
        Assert.That(File.Exists(executable), $"nethtest was not built next to the tests at {executable}");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable, args)
            {
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        // Both streams are drained before waiting, so neither can fill its buffer and block the run.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        using CancellationTokenSource timeout = new(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"nethtest did not exit within {Timeout}");
        }

        return (await stdout, await stderr);
    }
}
