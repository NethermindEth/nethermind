// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

/// <summary>Diagnostic-only counters: how often stride prefetch and SLOAD fusion fire on real workloads.</summary>
public static class DiagCounters
{
    public static long StrideAttempts;
    public static long StrideGranted;
    public static long SLoadFuseEvents;
    public static long SLoadFusedOps;

    public static void Report(string line) => System.Console.Out.WriteLine("DIAG " + line);
}
