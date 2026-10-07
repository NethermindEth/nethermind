// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>Experiment only: per-block diagnostics of the pre-warm handoff, written into the slow-block log.</summary>
/// <remarks>Enabled by NETHERMIND_EXP_HANDOFF_DIAG=1. Main-thread state except the statuses kept in <see cref="BlockFootprints"/>.</remarks>
public static class HandoffDiagnostics
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_EXP_HANDOFF_DIAG") == "1";

    public const int Replayed = 0, Rejected = 1, Missing = 2, NotEligible = 3, Failed = 4;
    private static readonly string[] OutcomeNames = ["replayed", "rejected", "missing", "not_eligible", "failed"];
    private static readonly long[] Counts = new long[OutcomeNames.Length];
    private static readonly long[] Ticks = new long[OutcomeNames.Length];
    private static readonly long[] Gas = new long[OutcomeNames.Length];

    internal static readonly string[] MismatchNames = ["none", "existence", "liveness", "nonce", "balance", "min_balance", "code", "slot"];
    private static readonly long[] Mismatches = new long[MismatchNames.Length];
    private static readonly long[] MismatchGas = new long[MismatchNames.Length];

    public const int NotStarted = 0, Running = 1, Stored = 2, Overtaken = 3, FailedRun = 4, Sender = 5, Unfunded = 6,
        OpaqueTryGetAccount = 7, OpaqueEmptyIfDeleted = 8, OpaqueReset = 9, OpaqueCommit = 10, OpaqueOverlay = 11,
        OpaqueAmbiguousRestore = 12, OpaqueUnplacedRestore = 13, OpaqueChangedRead = 14, OtherTransaction = 15, ChainQueued = 16;
    internal static readonly string[] StatusNames = ["not_started", "running", "stored", "overtaken", "failed_run", "sender", "unfunded",
        "opaque_try_get_account", "opaque_empty_if_deleted", "opaque_reset", "opaque_commit", "opaque_overlay",
        "opaque_ambiguous_restore", "opaque_unplaced_restore", "opaque_changed_read", "other_tx", "chain_queued"];

    public const int SlotRejectionHot = 0, SlotRejectionCap = 1, SlotRejectionMarkedPending = 2, SlotRejectionMarkedTaken = 3,
        SlotRejectionMarkedPassed = 4, SlotRejectionRewarmPending = 5, SlotRejectionRewarmTaken = 6, SlotRejectionRewarmPassed = 7,
        SlotRejectionRewarmMispredicted = 8, SlotRejectionRewarmUnexplained = 9, SlotRejectionNoWriter = 10, SlotRejectionMispredicted = 11,
        SlotRejectionUnmarkedOther = 12, SlotRejectionNoSlot = 13;
    private static readonly string[] SlotRejectionNames = ["hot", "cap", "marked_pending", "marked_taken", "marked_passed",
        "rewarm_pending", "rewarm_taken", "rewarm_passed", "rewarm_mispredicted", "rewarm_unexplained",
        "unmarked_no_writer", "unmarked_mispredicted", "unmarked_other", "no_slot"];
    private static readonly long[] SlotRejections = new long[SlotRejectionNames.Length];
    private static readonly long[] SlotRejectionGas = new long[SlotRejectionNames.Length];
    private static readonly long[] SlotRejectionTicks = new long[SlotRejectionNames.Length];
    private static int _lastSlotRejection = -1;

    // By tenth of the block: the outcomes, and the missing transactions by run status.
    private const int Deciles = 10;
    private static readonly int[] DecileStatuses = [NotStarted, Running, ChainQueued];
    private static readonly long[,] DecileCounts = new long[OutcomeNames.Length, Deciles];
    private static readonly long[,] DecileTicks = new long[OutcomeNames.Length, Deciles];
    private static readonly long[,] DecileAbsence = new long[DecileStatuses.Length, Deciles];
    private static readonly long[,] DecileAbsenceTicks = new long[DecileStatuses.Length, Deciles];
    private static int _txIndex;
    private static readonly long[] Absences = new long[StatusNames.Length];
    private static readonly long[] AbsenceGas = new long[StatusNames.Length];
    private static readonly long[] AbsenceTicks = new long[StatusNames.Length];

    private static readonly Dictionary<AddressAsKey, int> ReplayWrites = [];
    private static readonly Dictionary<AddressAsKey, int> ExecutedWrites = [];

    /// <summary>Set by block processing around transactions it executes, so their storage writes are attributed.</summary>
    public static bool Observing;

    internal static BlockFootprints? Block;
    private static long _firstTxStart, _lastTxEnd;
    private static int _lastAbsence = -1;
    private static long _built, _builtWrites, _buildTicks, _adopted, _unclaimed, _stale, _late;
    private static long _marked, _unchanged, _stored, _dropped, _overtaken, _rewarmTicks;

    internal static void TxStarted(long timestamp, int txIndex)
    {
        if (_firstTxStart == 0) _firstTxStart = timestamp;
        _txIndex = txIndex;
    }

    internal static void Count(int outcome, long start, Transaction tx)
    {
        long end = Stopwatch.GetTimestamp();
        Counts[outcome]++;
        Ticks[outcome] += end - start;
        Gas[outcome] += (long)tx.SpentGas;
        int txCount = Block?.Count ?? 0;
        int decile = txCount > 0 ? Math.Clamp(_txIndex * Deciles / txCount, 0, Deciles - 1) : 0;
        DecileCounts[outcome, decile]++;
        DecileTicks[outcome, decile] += end - start;
        if (outcome == Missing && _lastAbsence >= 0)
        {
            AbsenceTicks[_lastAbsence] += end - start;
            AbsenceGas[_lastAbsence] += (long)tx.SpentGas;
            int status = Array.IndexOf(DecileStatuses, _lastAbsence);
            if (status >= 0)
            {
                DecileAbsence[status, decile]++;
                DecileAbsenceTicks[status, decile] += end - start;
            }
        }

        if (outcome == Rejected && _lastSlotRejection >= 0) SlotRejectionTicks[_lastSlotRejection] += end - start;

        _lastAbsence = -1;
        _lastSlotRejection = -1;
        _lastTxEnd = end;
    }

    internal static void CountSlotRejection(int kind, Transaction tx)
    {
        SlotRejections[kind]++;
        SlotRejectionGas[kind] += (long)tx.GasLimit;
        _lastSlotRejection = kind;
    }

    internal static void CountMismatch(int code, Transaction tx)
    {
        Mismatches[code]++;
        MismatchGas[code] += (long)tx.GasLimit;
    }

    internal static void CountAbsence(int status)
    {
        Absences[status]++;
        _lastAbsence = status;
    }

    internal static void RecordReplayWrites(TransactionFootprint footprint)
    {
        foreach (ref readonly StateEffect effect in footprint.Effects)
        {
            if (effect.Kind != EffectKind.SetStorage) continue;
            ref int count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(ReplayWrites, effect.Address, out _);
            count++;
        }
    }

    internal static void RecordExecutedWrite(Address address)
    {
        ref int count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(ExecutedWrites, address, out _);
        count++;
    }

    private static readonly long[] _dryLast = new long[11];
    private static readonly long[] _dryAccountsLast = new long[6];

    // Per-block deltas of the dry run's process-wide sums; the maxima are taken and reset.
    private static void WriteDryRun(Utf8JsonWriter writer)
    {
        long[] now =
        [
            PredictedStorageCounters.DryExactAccounts, PredictedStorageCounters.DryExactWrites, PredictedStorageCounters.DryExactTicks,
            PredictedStorageCounters.DryInexactAccounts, PredictedStorageCounters.DryInexactWrites, PredictedStorageCounters.DryInexactMatched,
            PredictedStorageCounters.DryInexactLeftovers, PredictedStorageCounters.DryInexactTicks,
            PredictedStorageCounters.UnpredictedAccounts, PredictedStorageCounters.UnpredictedWrites, PredictedStorageCounters.UnpredictedTicks,
        ];
        string[] names = ["exact_accounts", "exact_writes", "exact_ms", "inexact_accounts", "inexact_writes", "inexact_matched", "inexact_leftovers",
            "inexact_ms", "unpredicted_accounts", "unpredicted_writes", "unpredicted_ms"];
        writer.WriteStartObject("storage_prediction");
        for (int i = 0; i < now.Length; i++)
        {
            long delta = now[i] - _dryLast[i];
            if (names[i].EndsWith("_ms", StringComparison.Ordinal)) writer.WriteNumber(names[i], Math.Round(delta * 1000.0 / Stopwatch.Frequency, 3));
            else writer.WriteNumber(names[i], delta);
            _dryLast[i] = now[i];
        }

        writer.WriteNumber("exact_max_ms", Math.Round(Interlocked.Exchange(ref PredictedStorageCounters.DryExactMaxTicks, 0) * 1000.0 / Stopwatch.Frequency, 3));
        writer.WriteNumber("inexact_max_ms", Math.Round(Interlocked.Exchange(ref PredictedStorageCounters.DryInexactMaxTicks, 0) * 1000.0 / Stopwatch.Frequency, 3));
        writer.WriteNumber("unpredicted_max_ms", Math.Round(Interlocked.Exchange(ref PredictedStorageCounters.UnpredictedMaxTicks, 0) * 1000.0 / Stopwatch.Frequency, 3));
        writer.WriteEndObject();

        long[] accounts =
        [
            PredictedStorageCounters.DryAccountsTotal, PredictedStorageCounters.DryAccountsExact, PredictedStorageCounters.DryAccountsInexact,
            PredictedStorageCounters.DryAccountsUnpredicted, PredictedStorageCounters.DryAccountsLeftover, PredictedStorageCounters.DryStateSetTicks,
        ];
        string[] accountNames = ["set", "exact", "inexact", "unpredicted", "leftover", "state_set_ms"];
        writer.WriteStartObject("account_prediction");
        for (int i = 0; i < accounts.Length; i++)
        {
            long delta = accounts[i] - _dryAccountsLast[i];
            if (i == accounts.Length - 1) writer.WriteNumber(accountNames[i], Math.Round(delta * 1000.0 / Stopwatch.Frequency, 3));
            else writer.WriteNumber(accountNames[i], delta);
            _dryAccountsLast[i] = accounts[i];
        }

        writer.WriteEndObject();
    }

    private static void WriteDeciles(Utf8JsonWriter writer, string name, long[,] counts, long[,] ticks, int row)
    {
        bool any = false;
        for (int d = 0; d < Deciles; d++) any |= counts[row, d] != 0;
        if (!any) return;
        writer.WriteStartArray(name);
        for (int d = 0; d < Deciles; d++) writer.WriteNumberValue(counts[row, d]);
        writer.WriteEndArray();
        writer.WriteStartArray(name + "_ms");
        for (int d = 0; d < Deciles; d++) writer.WriteNumberValue(Math.Round(ticks[row, d] * 1000.0 / Stopwatch.Frequency, 3));
        writer.WriteEndArray();
    }

    /// <summary>The block's diagnostics as a JSON object; clears them for the next block.</summary>
    public static string? TakeBlockJson()
    {
        ArrayBufferWriter<byte> buffer = new(1024);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            for (int i = 0; i < OutcomeNames.Length; i++)
            {
                writer.WriteNumber(OutcomeNames[i], Counts[i]);
                writer.WriteNumber(OutcomeNames[i] + "_ms", Math.Round(Ticks[i] * 1000.0 / Stopwatch.Frequency, 3));
                writer.WriteNumber(OutcomeNames[i] + "_gas", Gas[i]);
            }

            writer.WriteStartObject("mismatch");
            for (int i = 1; i < MismatchNames.Length; i++)
            {
                if (Mismatches[i] == 0) continue;
                writer.WriteNumber(MismatchNames[i], Mismatches[i]);
                writer.WriteNumber(MismatchNames[i] + "_gas_limit", MismatchGas[i]);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("slot_rejection");
            for (int i = 0; i < SlotRejectionNames.Length; i++)
            {
                if (SlotRejections[i] == 0) continue;
                writer.WriteNumber(SlotRejectionNames[i], SlotRejections[i]);
                writer.WriteNumber(SlotRejectionNames[i] + "_ms", Math.Round(SlotRejectionTicks[i] * 1000.0 / Stopwatch.Frequency, 3));
                writer.WriteNumber(SlotRejectionNames[i] + "_gas_limit", SlotRejectionGas[i]);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("deciles");
            for (int o = 0; o < OutcomeNames.Length; o++)
            {
                WriteDeciles(writer, OutcomeNames[o], DecileCounts, DecileTicks, o);
            }

            for (int s = 0; s < DecileStatuses.Length; s++)
            {
                WriteDeciles(writer, StatusNames[DecileStatuses[s]], DecileAbsence, DecileAbsenceTicks, s);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("absence");
            for (int i = 0; i < StatusNames.Length; i++)
            {
                if (Absences[i] == 0) continue;
                writer.WriteNumber(StatusNames[i], Absences[i]);
                writer.WriteNumber(StatusNames[i] + "_ms", Math.Round(AbsenceTicks[i] * 1000.0 / Stopwatch.Frequency, 3));
                writer.WriteNumber(StatusNames[i] + "_gas", AbsenceGas[i]);
            }

            writer.WriteEndObject();

            int replayOnly = 0, replayOnlySlots = 0, executedAccounts = 0, executedSlots = 0, both = 0;
            foreach (KeyValuePair<AddressAsKey, int> entry in ReplayWrites)
            {
                if (ExecutedWrites.ContainsKey(entry.Key))
                {
                    both++;
                }
                else
                {
                    replayOnly++;
                    replayOnlySlots += entry.Value;
                }
            }

            foreach (KeyValuePair<AddressAsKey, int> entry in ExecutedWrites)
            {
                executedAccounts++;
                executedSlots += entry.Value;
            }

            long built = PredictedStorageCounters.Built, builtWrites = PredictedStorageCounters.BuiltWrites, buildTicks = PredictedStorageCounters.BuildTicks;
            long adopted = PredictedStorageCounters.Adopted, unclaimed = PredictedStorageCounters.Unclaimed;
            long stale = PredictedStorageCounters.StaleBase, late = PredictedStorageCounters.Late;
            if (built != _built || late != _late)
            {
                writer.WriteStartObject("predicted_storage");
                writer.WriteNumber("built", built - _built);
                writer.WriteNumber("built_writes", builtWrites - _builtWrites);
                writer.WriteNumber("build_cpu_ms", Math.Round((buildTicks - _buildTicks) * 1000.0 / Stopwatch.Frequency, 3));
                writer.WriteNumber("adopted", adopted - _adopted);
                writer.WriteNumber("unclaimed_previous_block", unclaimed - _unclaimed);
                writer.WriteNumber("stale_base", stale - _stale);
                writer.WriteNumber("late", late - _late);
                writer.WriteEndObject();
            }

            (_built, _builtWrites, _buildTicks, _adopted, _unclaimed, _stale, _late) = (built, builtWrites, buildTicks, adopted, unclaimed, stale, late);

            if (PredictedStorageCounters.DryRun) WriteDryRun(writer);

            long marked = RewarmCounters.Marked, unchanged = RewarmCounters.Unchanged, stored = RewarmCounters.Stored;
            long dropped = RewarmCounters.Dropped, overtaken = RewarmCounters.Overtaken, rewarmTicks = RewarmCounters.Ticks;
            if (marked != _marked)
            {
                writer.WriteStartObject("rewarm");
                writer.WriteNumber("marked", marked - _marked);
                writer.WriteNumber("unchanged", unchanged - _unchanged);
                writer.WriteNumber("stored", stored - _stored);
                writer.WriteNumber("dropped", dropped - _dropped);
                writer.WriteNumber("overtaken", overtaken - _overtaken);
                writer.WriteNumber("run_ms", Math.Round((rewarmTicks - _rewarmTicks) * 1000.0 / Stopwatch.Frequency, 3));
                writer.WriteEndObject();
            }

            (_marked, _unchanged, _stored, _dropped, _overtaken, _rewarmTicks) = (marked, unchanged, stored, dropped, overtaken, rewarmTicks);

            writer.WriteStartObject("storage_writers");
            writer.WriteNumber("replay_only_accounts", replayOnly);
            writer.WriteNumber("replay_only_writes", replayOnlySlots);
            writer.WriteNumber("executed_accounts", executedAccounts);
            writer.WriteNumber("executed_writes", executedSlots);
            writer.WriteNumber("shared_accounts", both);
            writer.WriteEndObject();

            BlockFootprints? block = Block;
            if (block is not null && _firstTxStart != 0)
            {
                static double ToMs(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 3);
                long warmed = Volatile.Read(ref block.WarmedAt);
                writer.WriteNumber("prewarm_start_to_first_tx_ms", ToMs(_firstTxStart - block.CreatedAt));
                writer.WriteNumber("txs_ms", ToMs(_lastTxEnd - _firstTxStart));
                if (warmed != 0) writer.WriteNumber("warmup_done_before_last_tx_ms", ToMs(_lastTxEnd - warmed));
                long exhausted = Volatile.Read(ref block.JobsExhaustedAt);
                if (exhausted != 0)
                {
                    writer.WriteNumber("jobs_exhausted_after_first_tx_ms", ToMs(exhausted - _firstTxStart));
                    writer.WriteNumber("jobs_exhausted_before_last_tx_ms", ToMs(_lastTxEnd - exhausted));
                }
            }

            writer.WriteEndObject();
        }

        Array.Clear(Counts);
        Array.Clear(Ticks);
        Array.Clear(Gas);
        Array.Clear(Mismatches);
        Array.Clear(MismatchGas);
        Array.Clear(Absences);
        Array.Clear(AbsenceGas);
        Array.Clear(AbsenceTicks);
        Array.Clear(SlotRejections);
        Array.Clear(SlotRejectionGas);
        Array.Clear(SlotRejectionTicks);
        Array.Clear(DecileCounts);
        Array.Clear(DecileTicks);
        Array.Clear(DecileAbsence);
        Array.Clear(DecileAbsenceTicks);
        _lastSlotRejection = -1;
        ReplayWrites.Clear();
        ExecutedWrites.Clear();
        _firstTxStart = 0;
        _lastTxEnd = 0;
        _lastAbsence = -1;
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Experiment only: attributes the storage writes of transactions block processing executes.</summary>
public sealed class HandoffWriteObserver(IWorldState state) : WorldStateDecorator(state), IWorldState
{
    private static bool Observed => HandoffDiagnostics.Observing && ProcessingThread.IsBlockProcessingThread;

    public override void Set(in StorageCell storageCell, in UInt256 newValue)
    {
        if (Observed) HandoffDiagnostics.RecordExecutedWrite(storageCell.Address);
        base.Set(in storageCell, in newValue);
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue)
    {
        if (Observed) HandoffDiagnostics.RecordExecutedWrite(storageCell.Address);
        State.Set(in storageCell, in newValue, in currentValue);
    }

    public bool HasCode(Address address) => State.HasCode(address);

    public void MarkStorageDestroyed(Address address) => State.MarkStorageDestroyed(address);
}
