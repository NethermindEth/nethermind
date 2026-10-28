#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include metrics.helpers.cs
#:include ../sync/sync.helpers.cs
using System.Text.Json.Nodes;

try
{
    var network = Environment.GetEnvironmentVariable("NETWORK") ?? throw new InvalidOperationException("NETWORK is required");
    var mode = Environment.GetEnvironmentVariable("SYNC_MODE") ?? throw new InvalidOperationException("SYNC_MODE is required");
    var found = new Dictionary<string, DateTimeOffset>();
    var counters = new JsonObject();
    var log = Environment.GetEnvironmentVariable("LOG_FILE");
    using var cancellation = new CancellationTokenSource();
    using var signal = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (!string.IsNullOrEmpty(log))
    {
        foreach (var line in File.ReadLines(log)) Metrics.ParseLine(line, found, counters);
    }
    else
    {
        var container = Sync.Container(network);
        // Merge at the descriptor before reading: separate async readers can reorder duplicate stage markers.
        using var process = Sync.Start("bash", ["-c", "exec docker logs -t \"$1\" 2>&1", "--", container], redirect: true);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation.Token) is { } line) Metrics.ParseLine(line, found, counters);
            await process.WaitForExitAsync(cancellation.Token);
            if (process.ExitCode != 0) { Console.Error.WriteLine($"docker logs {container} failed with {process.ExitCode}"); return 1; }
        }
        finally { Sync.Stop(process); }
    }
    var record = Metrics.Record(network, mode, Environment.GetEnvironmentVariable("DOCKER_IMAGE"), found, counters);
    await File.WriteAllTextAsync(args[0], record.ToJsonString(), cancellation.Token);
    Console.WriteLine(record.ToJsonString(new() { WriteIndented = true }));
    return 0;
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
