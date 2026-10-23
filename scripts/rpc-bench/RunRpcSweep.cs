#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include SweepConfiguration.Helpers.cs
#:include SweepRunner.Helpers.cs
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkPaths.Helpers.cs

using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
using var termination = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; cancellation.Cancel(); }) : null;
try
{
    if (args is ["--arm-image", var entry])
    {
        var specification = entry.Split(['#', '+'], 2)[0];
        var separator = specification.IndexOf('@');
        if (separator >= 0) Console.WriteLine(specification[(separator + 1)..]);
        return 0;
    }
    if (args is ["--arm-label", var client, var image])
    {
        Console.WriteLine(SweepArm.Parse(client + "@" + image, "").Label);
        return 0;
    }
    var configuration = new SweepConfiguration(Environment.GetEnvironmentVariable);
    return await new SweepRunner(configuration).RunAsync(cancellation.Token);
}
catch (OperationCanceledException) { Console.Error.WriteLine("::error::sweep cancelled"); return 130; }
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or OverflowException)
{
    Console.Error.WriteLine("::error::" + exception.Message);
    return 1;
}
