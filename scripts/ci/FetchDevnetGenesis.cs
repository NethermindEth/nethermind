#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include FetchDevnetGenesis.Helpers.cs

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    return await DevnetGenesis.Fetch(Environment.GetEnvironmentVariable, DevnetGenesis.Execute, Console.Out, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
