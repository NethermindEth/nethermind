#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Gcp.Helpers.cs
#:include Lifecycle.Helpers.cs
using System.Runtime.InteropServices;

try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
    return await new Lifecycle(Gcp.Run, Environment.GetEnvironmentVariable, Task.Delay).Destroy(cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 143;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
