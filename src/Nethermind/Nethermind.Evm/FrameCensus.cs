// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Evm;

public static class FrameCensus
{
    private static long s_transactions;
    private static long s_opcodes;
    private static long s_frames;

    public static void CountFrame() => Interlocked.Increment(ref s_frames);

    public static void Observe(int opCodes)
    {
        long n = Interlocked.Increment(ref s_transactions);
        Interlocked.Add(ref s_opcodes, opCodes);
        if (n % 200 != 0) return;

        long ops = Interlocked.Read(ref s_opcodes);
        long frames = Interlocked.Read(ref s_frames);
        Console.WriteLine($"FRAME-CENSUS txs {n}, opcodes/tx {ops / (double)n:F0}, frames/tx {frames / (double)n:F1}, opcodes/frame {(frames == 0 ? 0 : ops / (double)frames):F1}");
    }
}
