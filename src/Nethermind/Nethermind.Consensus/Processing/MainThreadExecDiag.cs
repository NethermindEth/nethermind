// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// BENCH ONLY (branch bench/evm-per-contract-14393, not for merge). Where block processing spends its own time on the
/// transactions it executes instead of taking over a warm run: wall time per handoff outcome, and per top-level recipient
/// (<see cref="Transaction.To"/>, <see cref="Address.Zero"/> for creations), logged at INFO every <see cref="Window"/> blocks.
/// All calls come from the main processing thread.
/// </summary>
internal static class MainThreadExecDiag
{
    public const byte NotTried = 0, Missing = 1, Ineligible = 2, Rejected = 3, Failed = 4;
    private static readonly string[] Names = ["NotTried", "Missing", "Ineligible", "Rejected", "Failed"];
    private const int Window = 50;
    private const int Top = 25;

    private sealed class Acc
    {
        public double Ms;
        public long Gas;
        public int N;
        public int Rejected;
    }

    private static readonly ConcurrentDictionary<Address, Acc> ByTo = new();
    private static readonly double[] OutcomeMs = new double[5];
    private static readonly long[] OutcomeN = new long[5];
    private static readonly long[] OutcomeGas = new long[5];
    private static double _replayMs;
    private static long _replayN, _replayGas;
    private static ulong _lastBlock = ulong.MaxValue;
    private static int _blocks;

    public static void Replayed(double ms, long gas)
    {
        _replayMs += ms;
        _replayN++;
        _replayGas += gas;
    }

    public static void Executed(byte outcome, Transaction tx, double ms)
    {
        long gas = (long)tx.SpentGas;
        OutcomeMs[outcome] += ms;
        OutcomeN[outcome]++;
        OutcomeGas[outcome] += gas;
        Acc acc = ByTo.GetOrAdd(tx.To ?? Address.Zero, static _ => new Acc());
        acc.Ms += ms;
        acc.Gas += gas;
        acc.N++;
        if (outcome == Rejected) acc.Rejected++;
    }

    public static void OnBlock(BlockHeader header, ILogger logger)
    {
        if (header.Number == _lastBlock) return;
        _lastBlock = header.Number;
        if (++_blocks < Window) return;

        if (logger.IsInfo)
        {
            double b = _blocks;
            double total = OutcomeMs.Sum();
            StringBuilder sb = new();
            sb.Append($"EvmDiag blocks={_blocks} to={header.Number - 1} | main-thread execution ms/blk total={total / b:F2}:");
            for (int k = 0; k < Names.Length; k++)
            {
                sb.Append($" {Names[k]}={OutcomeMs[k] / b:F2}(n={OutcomeN[k] / b:F1},gas={OutcomeGas[k] / b / 1e6:F2}M)");
            }

            sb.Append($" | replay ms/blk={_replayMs / b:F2}(n={_replayN / b:F1},gas={_replayGas / b / 1e6:F2}M)");
            sb.Append(" | top recipients by main-thread ms/blk (n, rejected, gas M, share of total):");
            foreach ((Address to, Acc acc) in ByTo.OrderByDescending(static x => x.Value.Ms).Take(Top))
            {
                sb.Append($" {to}={acc.Ms / b:F3}({acc.N},{acc.Rejected},{acc.Gas / 1e6:F1},{(total > 0 ? 100 * acc.Ms / total : 0):F1}%)");
            }

            logger.Info(sb.ToString());
        }

        ByTo.Clear();
        System.Array.Clear(OutcomeMs);
        System.Array.Clear(OutcomeN);
        System.Array.Clear(OutcomeGas);
        _replayMs = 0;
        _replayN = _replayGas = 0;
        _blocks = 0;
    }
}
