// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;

namespace Nethermind.Logging;

/// <summary>
/// Lightweight global diagnostic logger for prewarmer/processing thread interleaving analysis.
/// Disabled by default — set <see cref="IsEnabled"/> to true to emit timestamped lines to stderr.
/// </summary>
public static class DiagnosticLogger
{
    public static volatile bool IsEnabled;

    public static void Log(string message)
    {
        if (!IsEnabled) return;
        long ts = Stopwatch.GetTimestamp();
        double ms = (double)ts / Stopwatch.Frequency * 1000.0;
        Console.Error.WriteLine($"[DIAG {ms:F3}ms T{Environment.CurrentManagedThreadId:D3}] {message}");
    }
}
