#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include sync/Sync.Helpers.cs

var network = Environment.GetEnvironmentVariable("NETWORK") ?? "";
var mode = Environment.GetEnvironmentVariable("SYNC_MODE") ?? "HalfPath";
if (!new[] { "flat", "halfpath" }.Contains(mode.ToLowerInvariant()))
{
    Console.WriteLine($"Unsupported sync mode: {mode}");
    return 1;
}
var flat = mode.Equals("flat", StringComparison.OrdinalIgnoreCase);
var bad = new[] { "Exception", flat ? "Verification failed" : "Missing node found!" };
var required = new Dictionary<string, int> { ["Processed"] = 20, [flat ? "Verification complete." : "Stats after finishing state"] = 1 };
if (flat) required["MismatchedAccounts=0, MismatchedSlots=0, MissingInFlat=0, MissingInTrie=0"] = 1;
if (!new[] { "joc-mainnet", "joc-testnet", "linea-mainnet", "linea-sepolia", "energyweb", "volta" }.Contains(network)) required["Synced Chain Head"] = 20;
var counts = required.Keys.ToDictionary(key => key, _ => 0);
using var cancellation = new CancellationTokenSource();
using var signal = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var process = Sync.Start("docker", ["logs", "-f", Sync.Container(network)], redirect: true);
var failed = false;
var extra = 0;
try
{
    while (await process.StandardOutput.ReadLineAsync(cancellation.Token) is { } line)
    {
        Console.WriteLine(line.Trim());
        if (failed)
        {
            if (++extra < 100) continue;
            Console.WriteLine("Exiting after capturing extra logs due to error.");
            return 1;
        }
        if (bad.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
        {
            Console.WriteLine($"Error: Found bad log in line: {line.Trim()}");
            failed = true;
            continue;
        }
        foreach (var marker in required.Keys)
            if (line.Contains(marker, StringComparison.Ordinal)) counts[marker]++;
        if (required.All(pair => counts[pair.Key] >= pair.Value))
        {
            Console.WriteLine("All required logs found.");
            return 0;
        }
    }
}
catch (Exception exception)
{
    Console.WriteLine($"An error occurred: {exception.Message}");
    return 1;
}
finally { Sync.Stop(process); }
Console.WriteLine("Unhandled termination. Probably critical issue in client. Stopping...");
return 1;
