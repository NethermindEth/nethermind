// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>Counts prewarm runs in progress, so a block-end fan-out can note how many still hold workers.</summary>
public static class PrewarmActivity
{
    private static int s_active;

    public static int Active => Volatile.Read(ref s_active);

    public static void Enter() => Interlocked.Increment(ref s_active);

    /// <summary>Ends a run; one that ends after its pass was cancelled is noted as pwx{runs left}@{µs}.</summary>
    public static void Exit(bool cancelled)
    {
        int left = Interlocked.Decrement(ref s_active);
        if (cancelled && NewPayloadTrace.Enabled) NewPayloadTrace.Note($"pwx{left}@{NewPayloadTrace.NowUs()}");
    }
}
