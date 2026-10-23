#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include StallWatchdog.Helpers.cs
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.FirstOrDefault() == "--child")
{
    await File.WriteAllTextAsync(args[1], Environment.ProcessId + "\n");
    var start = new ProcessStartInfo(args[2]) { UseShellExecute = false };
    foreach (var argument in args[3..]) start.ArgumentList.Add(argument);
    try
    {
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return exception.NativeErrorCode == 2 ? 127 : 126;
    }
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
try { return await StallWatchdog.Run(args, Environment.GetEnvironmentVariable, cancellation.Token); }
catch (OperationCanceledException) { return 143; }
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
