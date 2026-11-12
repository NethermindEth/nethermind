// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Reports the main thread's per-transaction progress to the prewarmer so it can skip warming already-started txs,
/// and takes over a transaction's warm run when its footprint matches the state.
/// The <see cref="IPrewarmerState.IsPrewarmer"/> guard ensures only the main execution reports, not the prewarmer's own scope.
/// </summary>
/// <remarks>Registered for main block processing only, so footprints are never taken by block building or tracing.</remarks>
public class PrewarmerTxAdapter(
    ITransactionProcessorAdapter baseAdapter,
    BlockCachePreWarmer preWarmer,
    IPrewarmerState prewarmerState,
    IWorldState worldState,
    ICodeInfoRepository codeInfoRepository,
    ILogManager logManager) : ITransactionProcessorAdapter
{
    private readonly ILogger _logger = logManager.GetClassLogger<PrewarmerTxAdapter>();
    private BlockExecutionContext _blockExecutionContext;

    internal (int Replayed, int Rejected, int Missing) Tally { get; private set; }

    [SkipLocalsInit]
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (!prewarmerState.IsPrewarmer)
        {
            preWarmer.OnBeforeTxExecution();
            ProbeBlock();
            TransactionFootprint? footprint = preWarmer.FindFootprint(transaction, _blockExecutionContext.Header, out bool eligible);
            long probeStart = Stopwatch.GetTimestamp();
            _probeClass = 6;
            _probeFootprint = null;
            int g0Bits = preWarmer.ProbeBitsAtMain();
            if (eligible && TryReplay(footprint, transaction, txTracer, out TransactionResult result))
            {
                ProbeAdd(0, transaction, probeStart);
                G0Add(footprint!.FromMempool ? 0 : 1, g0Bits, transaction, probeStart);
                return result;
            }

            TransactionResult executed = prewarmerState.CommittedWrites is { } committed && preWarmer.TakesExecutedWrites
                ? ExecuteReportingWrites(transaction, txTracer, committed)
                : baseAdapter.Execute(transaction, txTracer);
            int probeClass = _probeClass;
            if (probeClass == 4 && _probeFootprint is not null && ProbeIsAdditive(_probeFootprint, transaction)) probeClass = 5;
            ProbeAdd(probeClass, transaction, probeStart);
            if (probeClass == 1)
            {
                int missClass = ProbeMissClass(_probeMissBits);
                ProbeAdd(missClass, transaction, probeStart);
                if ((_probeMissBits & 512) != 0) ProbeAdd(17, transaction, probeStart);
                G0Add(missClass == 9 ? 4 : missClass == 7 ? 5 : 6, g0Bits, transaction, probeStart);
            }
            else if (_probeFootprint is not null)
            {
                G0Add(_probeFootprint!.FromMempool ? 2 : 3, g0Bits, transaction, probeStart);
            }
            else
            {
                G0Add(7, g0Bits, transaction, probeStart);
            }
            return executed;
        }

        return baseAdapter.Execute(transaction, txTracer);
    }

    // Apart, so the logging is no part of the frame of every replay.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Undo(Snapshot snapshot, Transaction tx, Exception ex)
    {
        worldState.Restore(snapshot);
        Blockchain.Metrics.PrewarmHandoffFailures++;
        if (_logger.IsDebug) _logger.Debug($"Executing transaction {tx.Hash}, its pre-warm footprint failed to apply: {ex}");
    }

    private TransactionResult ExecuteReportingWrites(Transaction transaction, ITxTracer txTracer, CommittedStorageWrites committed)
    {
        committed.Begin();
        try
        {
            return baseAdapter.Execute(transaction, txTracer);
        }
        finally
        {
            // Reported also when there are none: the writes its footprint predicted were not made.
            preWarmer.ReportExecutedWrites(committed.End());
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _blockExecutionContext = blockExecutionContext;
        baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
    }

    [SkipLocalsInit]
    private bool TryReplay(TransactionFootprint? footprint, Transaction tx, ITxTracer txTracer, out TransactionResult result)
    {
        result = default;
        if (footprint is null)
        {
            _probeClass = 1;
            _probeMissBits = preWarmer.ProbeBitsAtMain();
            Tally = Tally with { Missing = Tally.Missing + 1 };
            Blockchain.Metrics.PrewarmHandoffsMissing++;
            return false;
        }

        // Any tracer beyond the receipt would miss the execution that does not happen.
        if (txTracer is not BlockReceiptsTracer { IsTracingOnlyReceipts: true }) return false;

        BlockHeader header = _blockExecutionContext.Header;
        IReleaseSpec spec = _blockExecutionContext.Spec;
        // The pre-execution checks a warm run skips: the gas left in the block, and a sender with code (EIP-3607).
        if (tx.GasLimit > header.GasLimit - header.GasUsed || worldState.IsInvalidContractSender(spec, tx.SenderAddress!, codeInfoRepository)) return false;

        if (!footprint.Matches(worldState))
        {
            _probeClass = ProbeClassify(footprint);
            _probeFootprint = footprint;
            Tally = Tally with { Rejected = Tally.Rejected + 1 };
            Blockchain.Metrics.PrewarmHandoffsRejected++;
            return false;
        }

        Snapshot snapshot = worldState.TakeSnapshot();
        try
        {
            footprint.Replay(worldState, spec);
        }
        catch (Exception ex)
        {
            Undo(snapshot, tx, ex);
            return false;
        }

        worldState.Commit(spec, NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);

        ref readonly FootprintReceipt receipt = ref footprint.Receipt;
        GasConsumed gas = receipt.Gas;
        tx.SpentGas = gas.SpentGas;
        tx.BlockGasUsed = gas.EffectiveBlockGas;
        footprint.Counts.Flush();

        if (receipt.Success)
        {
            txTracer.MarkAsSuccess(receipt.Recipient, in gas, [], receipt.Logs);
        }
        else
        {
            txTracer.MarkAsFailed(receipt.Recipient, in gas, [], receipt.Error);
        }

        Tally = Tally with { Replayed = Tally.Replayed + 1 };
        Blockchain.Metrics.PrewarmHandoffs++;
        if (footprint.Refreshed) Blockchain.Metrics.PrewarmRefreshesTakenOver++;
        if (footprint.FromMempool) Blockchain.Metrics.PrewarmMempoolRunsTakenOver++;
        result = footprint.Result;
        return true;
    }
    private static readonly string[] ProbeNames = ["rep", "miss", "rej_acct", "rej_ro", "rej_rw", "rej_add", "other",
        "m_none", "m_late", "m_running", "m_op_acct", "m_op_restore", "m_op_value", "m_failed", "m_nonce", "m_plain", "m_exc", "m_disc", "m_stored", "m_other", "m_nofp", "m_op_misc"];
    private readonly long[] _probeCount = new long[22];
    private readonly long[] _probeGas = new long[22];
    private readonly long[] _probeTicks = new long[22];
    private int _probeMissBits;

    private static int ProbeMissClass(int bits)
    {
        if (bits < 0) return 20;
        if ((bits & 128) != 0) return 18;
        if ((bits & 8192) != 0) return 10;
        if ((bits & 16384) != 0) return 11;
        if ((bits & 32768) != 0) return 12;
        if ((bits & 65536) != 0) return 21;
        if ((bits & 16) != 0) return 12;
        if ((bits & 32) != 0) return 13;
        if ((bits & 64) != 0) return 14;
        if ((bits & (1024 | 2048)) != 0) return 16;
        if ((bits & 256) != 0) return 15;
        if ((bits & 4) != 0) return 9;
        if ((bits & 2) != 0) return 8;
        if (bits == 0 || bits == 512) return 7;
        return 19;
    }
    private readonly List<(StorageCell Cell, UInt256 Read, UInt256 Current)> _probeFailed = [];
    private long _probeBlock = -1;
    private int _probeClass;
    private TransactionFootprint? _probeFootprint;

    private void ProbeBlock()
    {
        if (_blockExecutionContext.Header is null) return;
        long number = (long)_blockExecutionContext.Header.Number;
        Hash256? hash = _blockExecutionContext.Header.Hash;
        if (number == _probeBlock && hash == _probeHash) return;
        if (_probeBlock >= 0)
        {
            G0Flush();
            System.Text.StringBuilder sb = new();
            sb.Append("HANDOFFPROBE block=").Append(_probeBlock);
            for (int i = 0; i < ProbeNames.Length; i++)
            {
                sb.Append(' ').Append(ProbeNames[i]).Append('=').Append(_probeCount[i]).Append('/').Append(_probeGas[i]).Append('/')
                    .Append(_probeTicks[i] * 1_000_000 / Stopwatch.Frequency);
            }
            Console.WriteLine(sb.ToString());
        }

        Array.Clear(_probeCount);
        Array.Clear(_probeGas);
        Array.Clear(_probeTicks);
        _probeBlock = number;
        _probeHash = hash;
    }

    private static readonly string[] G0Names = ["rep_mp", "rep_blk", "rej_mp", "rej_blk", "m_running", "m_none", "m_other", "other"];
    private readonly long[] _g0Count = new long[8];
    private readonly long[] _g0Gas = new long[8];
    private readonly long[] _g0Ticks = new long[8];
    private Hash256? _probeHash;
    private long _g0Seeded;
    private long _g0StoredMark = Blockchain.Metrics.PrewarmMempoolRunsStored;
    private long _g0TakenMark = Blockchain.Metrics.PrewarmMempoolRunsTakenOver;

    private void G0Add(int outcome, int bits, Transaction tx, long start)
    {
        _g0Ticks[outcome] += Stopwatch.GetTimestamp() - start;
        _g0Count[outcome]++;
        _g0Gas[outcome] += (long)tx.SpentGas;
        if (bits > 0 && (bits & 4096) != 0) _g0Seeded++;
    }

    private void G0Flush()
    {
        long stored = Blockchain.Metrics.PrewarmMempoolRunsStored;
        long taken = Blockchain.Metrics.PrewarmMempoolRunsTakenOver;
        long totalTicks = 0;
        foreach (long t in _g0Ticks) totalTicks += t;
        System.Text.StringBuilder sb = new();
        sb.Append("G0PROBE block=").Append(_probeBlock).Append(" hash=").Append(_probeHash)
            .Append(" tx_us=").Append(totalTicks * 1_000_000 / Stopwatch.Frequency);
        for (int i = 0; i < G0Names.Length; i++)
        {
            sb.Append(' ').Append(G0Names[i]).Append('=').Append(_g0Count[i]).Append('/').Append(_g0Gas[i]).Append('/')
                .Append(_g0Ticks[i] * 1_000_000 / Stopwatch.Frequency);
        }

        sb.Append(" mp_seeded=").Append(_g0Seeded).Append(" mp_stored_d=").Append(stored - _g0StoredMark).Append(" mp_taken_d=").Append(taken - _g0TakenMark);
        Console.WriteLine(sb.ToString());
        Array.Clear(_g0Count);
        Array.Clear(_g0Gas);
        Array.Clear(_g0Ticks);
        _g0Seeded = 0;
        _g0StoredMark = stored;
        _g0TakenMark = taken;
    }

    private void ProbeAdd(int probeClass, Transaction tx, long start)
    {
        _probeTicks[probeClass] += Stopwatch.GetTimestamp() - start;
        _probeCount[probeClass]++;
        _probeGas[probeClass] += (long)tx.SpentGas;
    }

    private int ProbeClassify(TransactionFootprint footprint)
    {
        try
        {
            _probeFailed.Clear();
            foreach (ref readonly AccountPrecondition account in footprint.Accounts)
            {
                if (!account.IsMet(worldState)) return 2;
            }

            bool readOnly = false;
            foreach (ref readonly SlotPrecondition slot in footprint.Slots)
            {
                worldState.Get(in slot.Cell, out UInt256 value);
                if (value == slot.Value) continue;
                if (slot.Written) _probeFailed.Add((slot.Cell, slot.Value, value));
                else readOnly = true;
            }

            return readOnly ? 3 : _probeFailed.Count > 0 ? 4 : 6;
        }
        catch
        {
            return 6;
        }
    }

    private bool ProbeIsAdditive(TransactionFootprint footprint, Transaction tx)
    {
        try
        {
            if (tx.SpentGas != footprint.Receipt.Gas.SpentGas) return false;
            foreach ((StorageCell cell, UInt256 read, UInt256 current) in _probeFailed)
            {
                UInt256 written = read;
                foreach (ref readonly StateEffect effect in footprint.Effects)
                {
                    if (effect.Kind == EffectKind.SetStorage && effect.Cell.Equals(cell)) written = effect.Value;
                }

                UInt256.Subtract(in written, in read, out UInt256 delta);
                UInt256.Add(in current, in delta, out UInt256 predicted);
                worldState.Get(in cell, out UInt256 actual);
                if (actual != predicted) return false;
            }

            foreach (ref readonly StateEffect effect in footprint.Effects)
            {
                if (effect.Kind != EffectKind.SetStorage) continue;
                bool failed = false;
                foreach ((StorageCell cell, _, _) in _probeFailed)
                {
                    if (cell.Equals(effect.Cell)) failed = true;
                }

                if (failed) continue;
                worldState.Get(in effect.Cell, out UInt256 actual);
                if (actual != effect.Value) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
