// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing;

public static class E1Probe
{
    private static readonly bool AnyThread = Environment.GetEnvironmentVariable("E1PROBE_ANY") == "1";
    private static volatile bool _active;
    private static long _blockStart;
    private static long _execStart;
    private static long _preTicks;
    private static long _execTicks;
    private static long _finTicks;
    private static long _rootTicks;
    private static long _it0Start;
    private static long _it0End;
    private static bool _parallel;
    private static long[] _start = new long[1024];
    private static long[] _end = new long[1024];
    private static int[] _worker = new int[1024];
    private static int _threads;
    private static readonly StringBuilder _sb = new(1 << 16);

    public static void Begin(Block block)
    {
        _active = false;
        if (!(ProcessingThread.IsBlockProcessingThread || AnyThread) || block.IsGenesis) return;
        int n = block.Transactions.Length;
        if (_start.Length < n)
        {
            int len = Math.Max(n, _start.Length * 2);
            _start = new long[len];
            _end = new long[len];
            _worker = new int[len];
        }

        Array.Clear(_start, 0, n);
        Array.Clear(_end, 0, n);
        _preTicks = _execTicks = _finTicks = _rootTicks = 0;
        _it0Start = _it0End = 0;
        _parallel = false;
        _threads = 0;
        _blockStart = Stopwatch.GetTimestamp();
        _active = true;
    }

    public static void PreDone()
    {
        if (!_active) return;
        _execStart = Stopwatch.GetTimestamp();
        _preTicks = _execStart - _blockStart;
    }

    public static void ExecDone()
    {
        if (!_active) return;
        _execTicks = Stopwatch.GetTimestamp() - _execStart;
    }

    public static void Parallel(int threads)
    {
        if (!_active) return;
        _parallel = true;
        _threads = threads;
    }

    public static long Now() => _active ? Stopwatch.GetTimestamp() : 0;

    public static void Tx(int index, long start)
    {
        if (!_active || start == 0) return;
        _start[index] = start;
        _end[index] = Stopwatch.GetTimestamp();
        _worker[index] = Environment.CurrentManagedThreadId;
    }

    public static void Iteration0(long start)
    {
        if (!_active || start == 0) return;
        _it0Start = start;
        _it0End = Stopwatch.GetTimestamp();
    }

    public static void Root(long start)
    {
        if (!_active || start == 0) return;
        _rootTicks += Stopwatch.GetTimestamp() - start;
    }

    public static void End(Block block, TxReceipt[] receipts, long finStart)
    {
        if (!_active) return;
        _active = false;
        _finTicks = Stopwatch.GetTimestamp() - finStart;
        StringBuilder sb = _sb.Clear();
        CultureInfo c = CultureInfo.InvariantCulture;
        sb.Append(c, $"E1PROBE block={block.Number} mode={(_parallel ? 'p' : 's')} n={block.Transactions.Length} cpu={_threads} gas={block.GasUsed} pre={Us(_preTicks)} exec={Us(_execTicks)} fin={Us(_finTicks)} root={Us(_rootTicks)}");
        if (_it0End != 0) sb.Append(c, $" it0={Us(_it0Start - _execStart)}:{Us(_it0End - _execStart)}");
        sb.Append(" tx=");
        Transaction[] txs = block.Transactions;
        for (int i = 0; i < txs.Length; i++)
        {
            ulong used = i < receipts.Length && receipts[i] is not null ? receipts[i].GasUsed : 0;
            if (_end[i] == 0)
            {
                sb.Append(c, $"-:-:0:{txs[i].GasLimit}:{used};");
                continue;
            }

            sb.Append(c, $"{Us(_start[i] - _execStart)}:{Us(_end[i] - _execStart)}:{_worker[i]}:{txs[i].GasLimit}:{used};");
        }

        sb.Append('\n');
        Console.Out.Write(sb.ToString());
    }

    private static long Us(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;
}
