#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RunTestProjects.Helpers.cs

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    return await TestProjects.Run(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable,
        TestProjects.Execute, Console.Out, cancellation.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
