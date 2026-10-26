#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkPaths.Helpers.cs

using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var scratch = BenchmarkProcess.EnvironmentValue("SCRATCH_ROOT");
    if (scratch.Length == 0) throw new ArgumentException("SCRATCH_ROOT: scratch root to clean");
    await BenchmarkPaths.ReapAsync(["rpcbench-", "nethermind-rpcbench", "ethcallchaos-bench", "jsonbench-"], cancellation.Token);
    try { scratch = await BenchmarkPaths.CanonicalAsync(scratch, false, cancellation.Token); }
    catch (IOException exception) { BenchmarkProcess.Log($"{exception.Message} — skipping scratch wipe"); return 0; }
    BenchmarkPaths.AssertSane(scratch, "SCRATCH_ROOT");
    var database = BenchmarkProcess.EnvironmentValue("DB_SOURCE");
    if (database.Length > 0 && Directory.Exists(database))
    {
        database = await BenchmarkPaths.CanonicalAsync(database, true, cancellation.Token);
        if (BenchmarkPaths.Contains(database, scratch)) throw new IOException("SCRATCH_ROOT resolves inside DB_SOURCE — refusing to clean");
        if (BenchmarkPaths.Contains(scratch, database)) throw new IOException("DB_SOURCE resolves inside SCRATCH_ROOT — refusing to clean");
    }
    foreach (var mount in BenchmarkPaths.MountsUnder(scratch))
    {
        BenchmarkProcess.Log($"Unmounting leftover mount: {mount}");
        if ((await BenchmarkProcess.RootAsync("umount", [mount], cancellation.Token)).Code != 0)
            await BenchmarkProcess.RootAsync("umount", ["-l", mount], cancellation.Token);
    }
    foreach (var name in new[] { "run", "run-reference", "diag", "ethcallchaos", "jsonbench", "parity", "warmup-cell", "arm" })
    {
        var target = Path.Combine(scratch, name);
        if (!File.Exists(target) && !Directory.Exists(target)) continue;
        var canonical = await BenchmarkPaths.CanonicalAsync(target, false, cancellation.Token);
        if (BenchmarkPaths.MountsUnder(canonical).Length > 0)
        {
            BenchmarkProcess.Log($"::warning::'{target}' still has live mounts — skipping its deletion"); continue;
        }
        // rm removes a symlink itself; it must not receive its resolved destination.
        await BenchmarkProcess.RootAsync("rm", ["-rf", "--", target], cancellation.Token);
    }
    BenchmarkProcess.Log("Defensive cleanup done.");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cleanup cancelled."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }
