// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// BENCH ONLY (bench/handoff-residual-diag, not for merge). For each transaction of a processed block: whether the
/// prewarm handoff took it over and, when it did not, why. The per-transaction slow-block JSON prints it.
/// </summary>
/// <remarks>
/// Logging only; nothing here feeds back into block processing.
/// The main thread records one outcome per transaction. A rejected transaction also gets all its preconditions evaluated
/// again: the reads <see cref="TransactionFootprint.Matches"/> makes, without its early exit. What the main thread spends
/// here is printed as <c>diag_us</c>, inside the transaction's <c>execution_ms</c>.
/// The warm and refresh threads write per-position states into <see cref="Positions"/>, racy by design.
/// Every main-thread call is guarded: a fault is counted and printed, never thrown.
/// On while the per-transaction slow-block log is (Blocks.SlowBlockPerTxThresholdMs >= 0), unless BENCH_HANDOFF_DIAG=0.
/// BENCH_HANDOFF_DIAG_FAIL=1 throws inside the guarded recording and the report of every 50th block, to exercise those paths.
/// </remarks>
internal static class HandoffDiag
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("BENCH_HANDOFF_DIAG") != "0";
    private static readonly bool FailTest = Environment.GetEnvironmentVariable("BENCH_HANDOFF_DIAG_FAIL") == "1";
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private static BlockRecords? _current;
    private static long _blocks;

    internal enum Outcome : byte
    {
        None,
        Taken,
        Rejected,
        Missing,
        GateTracer,
        GateGas,
        Gate3607,
        Ineligible,
        NoSession,
        Undone
    }

    internal enum WarmState : byte
    {
        NotStarted,
        Running,
        Stored,
        NoFootprint,
        Cut,
        Failed,
        Error,
        Cancelled,
        SkippedStarted,
        Plain
    }

    internal enum RefreshState : byte
    {
        None,
        Running,
        Stored,
        SkippedNotMoved,
        SkippedUntracked,
        SkippedNoFootprint,
        Failed,
        Cancelled,
        Error
    }

    /// <summary>Called on the block-processing thread before the block's transactions run.</summary>
    public static void Prepare(Block block)
    {
        if (!Enabled) return;
        _current = new BlockRecords(block.Transactions, Stopwatch.GetTimestamp(), FailTest && _blocks++ % 50 == 0);
    }

    /// <summary>Hands the block's records to the slow-block report; the next block starts new ones.</summary>
    public static BlockRecords? TakeRecords()
    {
        BlockRecords? current = _current;
        _current = null;
        return current;
    }

    /// <summary>The main thread starts <paramref name="tx"/>.</summary>
    public static void Arrive(Transaction tx)
    {
        if (_current is not { } snapshot) return;
        try
        {
            Transaction[] txs = snapshot.Transactions;
            int index = snapshot.Current + 1;
            if ((uint)index >= (uint)txs.Length || !ReferenceEquals(txs[index], tx))
            {
                index = -1;
                for (int i = 0; i < txs.Length; i++)
                {
                    if (ReferenceEquals(txs[i], tx))
                    {
                        index = i;
                        break;
                    }
                }
            }

            snapshot.Current = index;
            if (index < 0) return;
            snapshot.Records[index].ArrivalTicks = Stopwatch.GetTimestamp() - snapshot.Start;
            if (snapshot.FailTest && index == 1) throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL arrive");
        }
        catch (Exception ex)
        {
            Fault(snapshot, ex);
        }
    }

    /// <summary>The main thread replayed the footprint of the transaction it started.</summary>
    public static void Taken(TransactionFootprint footprint)
    {
        if (_current is not { } snapshot || snapshot.Current < 0) return;
        ref TxRecord record = ref snapshot.Records[snapshot.Current];
        record.Outcome = Outcome.Taken;
        record.Footprint = footprint;
    }

    /// <summary>The main thread executes the transaction it started, for <paramref name="outcome"/>.</summary>
    public static void NotTaken(Outcome outcome, TransactionFootprint? footprint, BlockFootprints? footprints, IWorldState state)
    {
        if (_current is not { } snapshot || snapshot.Current < 0) return;
        long start = Stopwatch.GetTimestamp();
        int position = snapshot.Current;
        ref TxRecord record = ref snapshot.Records[position];
        try
        {
            record.Outcome = outcome;
            record.Footprint = footprint;
            if (footprints is not null)
            {
                snapshot.Footprints ??= footprints;
                Capture(ref record, footprints, position, start);
            }

            if (outcome == Outcome.Rejected && footprint is not null) EvaluateFailures(ref record, footprint, state);
        }
        catch (Exception ex)
        {
            Fault(snapshot, ex);
        }
        finally
        {
            record.DiagTicks = Stopwatch.GetTimestamp() - start;
        }
    }

    /// <summary>The storage writes the executed transaction committed; passed through.</summary>
    public static ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Executed(ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes)
    {
        if (_current is { } snapshot && snapshot.Current >= 0)
        {
            ref TxRecord record = ref snapshot.Records[snapshot.Current];
            record.Executed = writes;
            record.Reported = true;
        }

        return writes;
    }

    private static void Fault(BlockRecords snapshot, Exception ex)
    {
        snapshot.Errors++;
        snapshot.LastError ??= $"{ex.GetType().Name}: {ex.Message}";
    }

    private static void Capture(ref TxRecord record, BlockFootprints footprints, int position, long now)
    {
        Positions positions = footprints.Diag;
        if ((uint)position >= (uint)positions.Warm.Length) return;
        record.Warm = (WarmState)Volatile.Read(ref positions.Warm[position]);
        record.WarmReason = positions.WarmReason[position];
        if (record.Warm == WarmState.Running) record.WarmElapsed = now - positions.WarmStart[position];
        record.Refresh = (RefreshState)Volatile.Read(ref positions.Refresh[position]);
        if (record.Refresh == RefreshState.Running) record.RefreshElapsed = now - positions.RefreshStart[position];
        record.Refreshes = positions.Refreshes[position];
        record.Invalidations = positions.Invalidations[position];
        record.InvalidatedAtMain = positions.InvalidatedAtMain[position];
        if (record.Invalidations > 0) record.InvalidatedAgo = now - positions.InvalidatedAt[position];
        record.Drops = positions.Drops[position];
        record.Pending = footprints.DiagIsPending(position);
        record.PendingCount = footprints.DiagPendingCount;
        record.Queued = Volatile.Read(ref positions.Queued);
    }

    // Matches without its early exit: the first account and the first slot that no longer hold, and how many do not.
    private static void EvaluateFailures(ref TxRecord record, TransactionFootprint footprint, IWorldState state)
    {
        foreach (ref readonly AccountPrecondition account in footprint.Accounts)
        {
            AccountFields field = FailedField(in account, state);
            if (field == AccountFields.None) continue;
            if (record.FailedAccounts++ == 0)
            {
                record.FailAccount = account.Address;
                record.FailField = field;
            }
        }

        foreach (ref readonly SlotPrecondition slot in footprint.Slots)
        {
            state.Get(in slot.Cell, out UInt256 value);
            if (value == slot.Value) continue;
            if (record.FailedSlots++ == 0)
            {
                record.HasFailSlot = true;
                record.FailSlot = slot.Cell;
            }
        }
    }

    // The first check of AccountPrecondition.IsMet that fails, in its order.
    private static AccountFields FailedField(in AccountPrecondition account, IWorldState state)
    {
        AccountFields fields = account.Fields;
        Address address = account.Address;
        if ((fields & AccountFields.Existence) != 0 && state.AccountExists(address) != account.Exists) return AccountFields.Existence;
        if ((fields & AccountFields.Liveness) != 0 && state.IsDeadAccount(address) != account.IsDead) return AccountFields.Liveness;
        if ((fields & AccountFields.Nonce) != 0 && state.GetNonce(address) != account.Nonce) return AccountFields.Nonce;
        if ((fields & (AccountFields.Balance | AccountFields.MinimumBalance)) != 0)
        {
            ref readonly UInt256 balance = ref state.GetBalance(address);
            if ((fields & AccountFields.Balance) != 0 && balance != account.Balance) return AccountFields.Balance;
            if (balance < account.MinimumBalance) return AccountFields.MinimumBalance;
        }

        if ((fields & AccountFields.Code) != 0 && state.GetCodeHash(address) != account.CodeHash) return AccountFields.Code;
        return AccountFields.None;
    }

    /// <summary>Writes the handoff fields of the transaction at <paramref name="index"/> into its slow-block entry.</summary>
    public static void WriteTransaction(Utf8JsonWriter writer, BlockRecords? snapshot, Transaction[] txs, int index, Address? coinbase)
    {
        if (snapshot is null || !ReferenceEquals(snapshot.Transactions, txs) || (uint)index >= (uint)snapshot.Records.Length) return;
        try
        {
            if (snapshot.FailTest && index == 2) throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL report");
            WriteRecord(writer, snapshot, index, coinbase);
        }
        catch (Exception ex)
        {
            snapshot.ReportErrors++;
            snapshot.LastReportError ??= $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void WriteRecord(Utf8JsonWriter writer, BlockRecords snapshot, int index, Address? coinbase)
    {
        ref TxRecord r = ref snapshot.Records[index];
        Transaction tx = snapshot.Transactions[index];
        writer.WriteString("handoff", Name(r.Outcome));
        writer.WriteNumber("at_ms", Math.Round(r.ArrivalTicks * MsPerTick, 3));
        if (r.DiagTicks > 0) writer.WriteNumber("diag_us", Math.Round(r.DiagTicks * MsPerTick * 1000, 1));
        if (r.Reported) writer.WriteNumber("writes", r.Executed.Length);
        Positions? positions = snapshot.Footprints?.Diag;
        if (positions is not null && positions.Discovery[index]) writer.WriteBoolean("disc", true);
        switch (r.Outcome)
        {
            case Outcome.Taken:
                writer.WriteString("src", Source(r.Footprint));
                break;
            case Outcome.Rejected:
                WriteRejected(writer, snapshot, index, ref r, tx, coinbase, positions);
                break;
            case Outcome.Missing:
                writer.WriteString("cause", WarmName(r.Warm, r.WarmReason));
                if (r.Warm == WarmState.Running) writer.WriteNumber("warm_ms", Math.Round(r.WarmElapsed * MsPerTick, 3));
                if (positions is not null)
                {
                    WarmState end = (WarmState)positions.Warm[index];
                    if (end != r.Warm) writer.WriteString("warm_end", WarmName(end, positions.WarmReason[index]));
                    if (positions.Mempool[index] != 0) writer.WriteString("mp", MempoolName(positions.Mempool[index]));
                }

                break;
            case Outcome.GateTracer:
            case Outcome.GateGas:
            case Outcome.Gate3607:
                writer.WriteString("cause", r.Outcome == Outcome.GateTracer ? "tracer" : r.Outcome == Outcome.GateGas ? "gas" : "3607");
                break;
            case Outcome.Ineligible:
                writer.WriteString("cause", tx.SenderAddress is null ? "nosender" : tx.SupportsFrames ? "frames" : tx.IsSystem() ? "system"
                    : tx.Nonce == ulong.MaxValue ? "nonce" : "zerofee");
                break;
        }
    }

    private static void WriteRejected(Utf8JsonWriter writer, BlockRecords snapshot, int index, ref TxRecord r, Transaction tx, Address? coinbase, Positions? positions)
    {
        TransactionFootprint? footprint = r.Footprint;
        writer.WriteString("src", Source(footprint));
        if (r.FailedAccounts > 0)
        {
            writer.WriteString("fail", $"acct:{r.FailField}:{Role(r.FailAccount!, tx, coinbase)}");
            writer.WriteString("fail_addr", r.FailAccount!.ToString());
        }
        else if (r.HasFailSlot)
        {
            writer.WriteString("fail", $"slot:{Role(r.FailSlot.Address, tx, coinbase)}");
            writer.WriteString("fail_addr", r.FailSlot.Address.ToString());
        }
        else
        {
            writer.WriteString("fail", "none");
        }

        writer.WriteNumber("nacct", r.FailedAccounts);
        writer.WriteNumber("nslot", r.FailedSlots);
        int distance = 0;
        if (r.HasFailSlot)
        {
            if (r.FailedAccounts > 0) writer.WriteString("slot_addr", r.FailSlot.Address.ToString());
            (distance, Outcome writerOutcome) = LastWriter(snapshot, index, in r.FailSlot);
            writer.WriteNumber("writer", distance);
            if (distance > 0) writer.WriteString("writer_ho", Name(writerOutcome));
        }

        int lead = r.Invalidations > 0 ? index - r.InvalidatedAtMain : 0;
        writer.WriteNumber("inv", r.Invalidations);
        if (r.Invalidations > 0)
        {
            writer.WriteNumber("inv_lead", lead);
            writer.WriteNumber("inv_ago_ms", Math.Round(r.InvalidatedAgo * MsPerTick, 3));
        }

        writer.WriteNumber("drops", r.Drops);
        writer.WriteBoolean("pend", r.Pending);
        writer.WriteNumber("pendn", r.PendingCount);
        writer.WriteNumber("q", r.Queued);
        writer.WriteNumber("refreshes", r.Refreshes);
        writer.WriteString("rstate", RefreshName(r.Refresh));
        if (r.Refresh == RefreshState.Running) writer.WriteNumber("r_ms", Math.Round(r.RefreshElapsed * MsPerTick, 3));
        bool untracked = footprint is not null && snapshot.Footprints is { } footprints && footprints.ReadsUntrackedSlot(footprint);
        if (untracked) writer.WriteBoolean("untracked", true);
        bool overflow = positions is not null && positions.ReaderOverflow[index];
        if (overflow) writer.WriteBoolean("rd_ovf", true);
        bool newSlot = false;
        if (positions is not null)
        {
            RefreshState end = (RefreshState)positions.Refresh[index];
            if (end != r.Refresh) writer.WriteString("rstate_end", RefreshName(end));
            if (footprint is { Refreshed: true } && r.HasFailSlot && positions.BeforeRefresh[index] is { } before)
            {
                newSlot = !Reads(before, in r.FailSlot);
                writer.WriteBoolean("new_slot", newSlot);
            }
        }

        writer.WriteString("cause", Cause(in r, footprint, distance, lead, untracked, overflow, newSlot));
    }

    // A first reading of the rejection; the analysis works from the raw fields.
    private static string Cause(in TxRecord r, TransactionFootprint? footprint, int writerDistance, int lead, bool untracked, bool overflow, bool newSlot)
    {
        if (r.FailedAccounts > 0) return "acct";
        if (!r.HasFailSlot) return "unknown";
        if (untracked) return "untracked";
        if (writerDistance == 0) return "no_writer";
        if (r.Invalidations == 0) return overflow ? "rd_overflow" : r.Queued > 0 ? "uninvalidated_queued" : "uninvalidated";
        if (footprint is { Refreshed: true }) return newSlot ? "refreshed_new_slot" : lead <= 1 ? "refreshed_late" : "refreshed_stale";
        if (lead <= 1) return "late";
        return r.Refresh switch
        {
            RefreshState.Running => "refresh_running",
            RefreshState.SkippedNotMoved or RefreshState.SkippedUntracked or RefreshState.SkippedNoFootprint => "refresh_skipped",
            RefreshState.Failed or RefreshState.Error => "refresh_failed",
            RefreshState.Cancelled => "refresh_cancelled",
            RefreshState.Stored => "refresh_stored",
            _ => r.Pending ? "pending" : r.Drops > 0 ? "dropped" : "untaken"
        };
    }

    // The nearest transaction before the position that wrote the slot: how far back, and what happened to it; 0 when none did.
    private static (int Distance, Outcome Outcome) LastWriter(BlockRecords snapshot, int index, in StorageCell cell)
    {
        for (int q = index - 1; q >= 0; q--)
        {
            ref TxRecord w = ref snapshot.Records[q];
            if (w.Outcome == Outcome.Taken)
            {
                if (w.Footprint is { } footprint && Writes(footprint, in cell)) return (index - q, Outcome.Taken);
            }
            else if (w.Reported)
            {
                foreach ((StorageCell Cell, UInt256 Value) write in w.Executed.Span)
                {
                    if (write.Cell.Equals(in cell)) return (index - q, w.Outcome);
                }
            }
        }

        return (0, Outcome.None);
    }

    private static bool Writes(TransactionFootprint footprint, in StorageCell cell)
    {
        foreach (ref readonly StateEffect effect in footprint.Effects)
        {
            if (effect.Kind == EffectKind.SetStorage ? effect.Cell.Equals(in cell)
                : effect.Kind is EffectKind.ClearStorage or EffectKind.MarkStorageDestroyed && effect.Address == cell.Address)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Reads(TransactionFootprint footprint, in StorageCell cell)
    {
        foreach (ref readonly SlotPrecondition read in footprint.Slots)
        {
            if (read.Cell.Equals(in cell)) return true;
        }

        return false;
    }

    /// <summary>Writes the block's handoff counters after its transactions.</summary>
    public static void WriteBlock(Utf8JsonWriter writer, BlockRecords? snapshot, Transaction[] txs)
    {
        if (snapshot is null || !ReferenceEquals(snapshot.Transactions, txs)) return;
        writer.WriteStartObject("handoff");
        try
        {
            Span<int> outcomes = stackalloc int[(int)Outcome.Undone + 1];
            foreach (ref readonly TxRecord record in snapshot.Records.AsSpan()) outcomes[(int)record.Outcome]++;
            for (int i = 0; i < outcomes.Length; i++)
            {
                if (outcomes[i] > 0) writer.WriteNumber(CountName((Outcome)i), outcomes[i]);
            }

            if (snapshot.Footprints is { } footprints)
            {
                Positions positions = footprints.Diag;
                writer.WriteNumber("refreshes_run", footprints.RefreshesRun);
                writer.WriteNumber("invalidations", positions.InvalidationsTotal);
                writer.WriteNumber("dropped", positions.Dropped);
                writer.WriteNumber("queue_max", positions.QueueMax);
                writer.WriteNumber("read_overflows", positions.ReaderOverflows);
                writer.WriteNumber("untracked_slots", footprints.DiagUntracked);
                int refreshed = 0, cancelled = 0, stored = 0, skipped = 0, failed = 0;
                long refreshTicks = 0;
                for (int i = 0; i < positions.Refresh.Length; i++)
                {
                    refreshed += positions.Refreshes[i];
                    refreshTicks += positions.RefreshTicks[i];
                    switch ((RefreshState)positions.Refresh[i])
                    {
                        case RefreshState.Cancelled: cancelled++; break;
                        case RefreshState.Stored: stored++; break;
                        case RefreshState.SkippedNotMoved or RefreshState.SkippedUntracked or RefreshState.SkippedNoFootprint: skipped++; break;
                        case RefreshState.Failed or RefreshState.Error: failed++; break;
                    }
                }

                writer.WriteNumber("refresh_runs", refreshed);
                writer.WriteNumber("refresh_ms", Math.Round(refreshTicks * MsPerTick, 3));
                writer.WriteNumber("refresh_end_stored", stored);
                writer.WriteNumber("refresh_end_cancelled", cancelled);
                writer.WriteNumber("refresh_end_skipped", skipped);
                writer.WriteNumber("refresh_end_failed", failed);
            }
        }
        catch (Exception ex)
        {
            snapshot.ReportErrors++;
            snapshot.LastReportError ??= $"{ex.GetType().Name}: {ex.Message}";
        }

        writer.WriteNumber("diag_errors", snapshot.Errors);
        if (snapshot.LastError is not null) writer.WriteString("diag_error", snapshot.LastError);
        writer.WriteNumber("report_errors", snapshot.ReportErrors);
        if (snapshot.LastReportError is not null) writer.WriteString("report_error", snapshot.LastReportError);
        writer.WriteEndObject();
    }

    private static string Source(TransactionFootprint? footprint) =>
        footprint is null ? "none" : footprint.Refreshed ? "refresh" : footprint.FromMempool ? "mempool" : "block";

    private static string Role(Address address, Transaction tx, Address? coinbase) =>
        address == tx.SenderAddress ? "sender"
        : address == tx.To ? "to"
        : address == coinbase ? "coinbase"
        : "other";

    private static string Name(Outcome outcome) => outcome switch
    {
        Outcome.Taken => "taken",
        Outcome.Rejected => "rejected",
        Outcome.Missing => "missing",
        Outcome.GateTracer or Outcome.GateGas or Outcome.Gate3607 => "gate",
        Outcome.Ineligible => "ineligible",
        Outcome.NoSession => "nosession",
        Outcome.Undone => "undone",
        _ => "none"
    };

    private static string CountName(Outcome outcome) => outcome switch
    {
        Outcome.GateTracer => "gate_tracer",
        Outcome.GateGas => "gate_gas",
        Outcome.Gate3607 => "gate_3607",
        _ => Name(outcome)
    };

    private static string WarmName(WarmState state, byte reason) => state switch
    {
        WarmState.NotStarted => "notstarted",
        WarmState.Running => "running",
        WarmState.Stored => "stored",
        WarmState.NoFootprint => $"nofp:{ReasonName(reason)}",
        WarmState.Cut => "cut",
        WarmState.Failed => $"failed:{(TransactionResult.ErrorType)reason}",
        WarmState.Error => "error",
        WarmState.Cancelled => "cancelled",
        WarmState.SkippedStarted => "skipped",
        WarmState.Plain => "plain",
        _ => "unknown"
    };

    private static string RefreshName(RefreshState state) => state switch
    {
        RefreshState.None => "none",
        RefreshState.Running => "running",
        RefreshState.Stored => "stored",
        RefreshState.SkippedNotMoved => "skip_notmoved",
        RefreshState.SkippedUntracked => "skip_untracked",
        RefreshState.SkippedNoFootprint => "skip_nofp",
        RefreshState.Failed => "failed",
        RefreshState.Cancelled => "cancelled",
        RefreshState.Error => "error",
        _ => "unknown"
    };

    private static string MempoolName(byte state) => state switch
    {
        1 => "seeded",
        2 => "context",
        3 => "coinbase",
        4 => "fee",
        _ => "unknown"
    };

    /// <summary>Why <see cref="FootprintRecorder.Finish"/> returned no footprint.</summary>
    internal static class Reason
    {
        public const byte Restore = 1;
        public const byte TryGetAccount = 2;
        public const byte ReadNonce = 3;
        public const byte ReadBalance = 4;
        public const byte ReadCode = 5;
        public const byte ReadExists = 6;
        public const byte ReadDead = 7;
        public const byte ReadOriginalBalance = 8;
        public const byte ReadOriginalSlot = 9;
        public const byte ReadSlot = 10;
        public const byte EmptyIfDeleted = 11;
        public const byte TransientCode = 12;
        public const byte Reset = 13;
        public const byte Commit = 14;
        public const byte Overlay = 15;
        public const byte Failed = 30;
        public const byte NoResult = 31;
        public const byte NoSender = 32;
        public const byte SenderNonce = 33;
    }

    private static string ReasonName(byte reason) => reason switch
    {
        Reason.Restore => "restore",
        Reason.TryGetAccount => "trygetaccount",
        Reason.ReadNonce => "read_nonce",
        Reason.ReadBalance => "read_balance",
        Reason.ReadCode => "read_code",
        Reason.ReadExists => "read_exists",
        Reason.ReadDead => "read_dead",
        Reason.ReadOriginalBalance => "read_origbalance",
        Reason.ReadOriginalSlot => "read_origslot",
        Reason.ReadSlot => "read_slot",
        Reason.EmptyIfDeleted => "emptyifdeleted",
        Reason.TransientCode => "transientcode",
        Reason.Reset => "reset",
        Reason.Commit => "commit",
        Reason.Overlay => "overlay",
        Reason.Failed => "failed",
        Reason.NoResult => "noresult",
        Reason.NoSender => "nosender",
        Reason.SenderNonce => "sendernonce",
        _ => "unknown"
    };

    /// <summary>One transaction of the block, as the main thread saw it.</summary>
    internal struct TxRecord
    {
        public Outcome Outcome;
        public TransactionFootprint? Footprint;
        public ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Executed;
        public bool Reported;
        public long ArrivalTicks;
        public long DiagTicks;
        // Its position when the main thread reached it.
        public WarmState Warm;
        public byte WarmReason;
        public long WarmElapsed;
        public RefreshState Refresh;
        public long RefreshElapsed;
        public short Refreshes;
        public short Invalidations;
        public int InvalidatedAtMain;
        public long InvalidatedAgo;
        public short Drops;
        public bool Pending;
        public int PendingCount;
        public int Queued;
        // A rejection: the first account and the first slot that no longer hold, and how many of each.
        public Address? FailAccount;
        public AccountFields FailField;
        public bool HasFailSlot;
        public StorageCell FailSlot;
        public short FailedAccounts;
        public short FailedSlots;
    }

    /// <summary>The records of one block.</summary>
    internal sealed class BlockRecords(Transaction[] transactions, long start, bool failTest)
    {
        public readonly Transaction[] Transactions = transactions;
        public readonly TxRecord[] Records = new TxRecord[transactions.Length];
        public readonly long Start = start;
        public readonly bool FailTest = failTest;
        public BlockFootprints? Footprints;
        public int Current = -1;
        public int Errors;
        public string? LastError;
        public int ReportErrors;
        public string? LastReportError;
    }

    /// <summary>The per-position states of one block's footprints, written by the warm and refresh threads.</summary>
    internal sealed class Positions(int count)
    {
        public readonly byte[] Warm = new byte[count];
        public readonly byte[] WarmReason = new byte[count];
        public readonly long[] WarmStart = new long[count];
        public readonly byte[] Refresh = new byte[count];
        public readonly short[] Refreshes = new short[count];
        public readonly long[] RefreshStart = new long[count];
        public readonly long[] RefreshTicks = new long[count];
        public readonly short[] Invalidations = new short[count];
        public readonly int[] InvalidatedAtMain = new int[count];
        public readonly long[] InvalidatedAt = new long[count];
        public readonly short[] Drops = new short[count];
        public readonly bool[] ReaderOverflow = new bool[count];
        public readonly bool[] Discovery = new bool[count];
        public readonly byte[] Mempool = new byte[count];
        public readonly TransactionFootprint?[] BeforeRefresh = new TransactionFootprint?[count];
        public IBlockProcessingProgress? Progress;
        public int Queued;
        public int QueueMax;
        public int InvalidationsTotal;
        public int Dropped;
        public int ReaderOverflows;

        public void WarmStarted(int position)
        {
            if ((uint)position >= (uint)Warm.Length) return;
            WarmStart[position] = Stopwatch.GetTimestamp();
            Volatile.Write(ref Warm[position], (byte)WarmState.Running);
        }

        public void WarmEnded(int position, WarmState state, byte reason = 0)
        {
            if ((uint)position >= (uint)Warm.Length) return;
            WarmReason[position] = reason;
            Volatile.Write(ref Warm[position], (byte)state);
        }

        public void RefreshStarted(int position, TransactionFootprint invalidated)
        {
            if ((uint)position >= (uint)Refresh.Length) return;
            BeforeRefresh[position] = invalidated;
            Refreshes[position]++;
            RefreshStart[position] = Stopwatch.GetTimestamp();
            Volatile.Write(ref Refresh[position], (byte)RefreshState.Running);
        }

        public void RefreshEnded(int position, RefreshState state)
        {
            if ((uint)position >= (uint)Refresh.Length) return;
            if (Refresh[position] == (byte)RefreshState.Running) RefreshTicks[position] += Stopwatch.GetTimestamp() - RefreshStart[position];
            Volatile.Write(ref Refresh[position], (byte)state);
        }

        /// <remarks>Under the footprints' lock.</remarks>
        public void Invalidated(int position)
        {
            if ((uint)position >= (uint)Invalidations.Length) return;
            Invalidations[position]++;
            InvalidatedAtMain[position] = Progress?.MainThreadTxIndex ?? -1;
            InvalidatedAt[position] = Stopwatch.GetTimestamp();
            InvalidationsTotal++;
        }

        /// <remarks>Under the footprints' lock.</remarks>
        public void DroppedUnrefreshed(int word, ulong dropped)
        {
            while (dropped != 0)
            {
                int position = (word << 6) + BitOperations.TrailingZeroCount(dropped);
                dropped &= dropped - 1;
                if ((uint)position < (uint)Drops.Length) Drops[position]++;
                Dropped++;
            }
        }

        /// <remarks>Under the footprints' lock.</remarks>
        public void ReadNotIndexed(int position)
        {
            if ((uint)position >= (uint)ReaderOverflow.Length) return;
            if (!ReaderOverflow[position]) ReaderOverflows++;
            ReaderOverflow[position] = true;
        }

        public void Discovered(int position)
        {
            if ((uint)position < (uint)Discovery.Length) Discovery[position] = true;
        }

        public void MempoolSeed(int position, byte state)
        {
            if ((uint)position < (uint)Mempool.Length) Mempool[position] = state;
        }

        public void Queue()
        {
            int queued = Interlocked.Increment(ref Queued);
            if (queued > QueueMax) QueueMax = queued;
        }

        public void Dequeue() => Interlocked.Decrement(ref Queued);
    }
}
