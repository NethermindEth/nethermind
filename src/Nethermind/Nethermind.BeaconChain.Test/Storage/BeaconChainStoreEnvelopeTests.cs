// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Storage;

// gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange/ByRoot: envelopes are served over
// [max(GLOAS_FORK_EPOCH, current_epoch - compute_min_epochs_for_block_requests()), current_epoch], so they must outlive a restart.
public class BeaconChainStoreEnvelopeTests
{
    private static readonly ulong SlotsPerEpoch = Sepolia.SlotsPerEpoch;

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Stored_envelope_reads_back_from_a_new_store_over_the_same_database(bool valid, bool verdictWithEnvelope)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        SignedExecutionPayloadEnvelope envelope = Envelope(FirstGloasSlot + 3);
        new BeaconChainStore(db, Sepolia).PutExecutionPayloadEnvelope(RootOf(envelope), envelope, valid && verdictWithEnvelope);
        if (valid && !verdictWithEnvelope) new BeaconChainStore(db, Sepolia).SetExecutionPayloadValid(RootOf(envelope));

        BeaconChainStore reopened = new(db, Sepolia);

        Assert.That(reopened.TryGetExecutionPayloadEnvelope(RootOf(envelope), out SignedExecutionPayloadEnvelope? read), Is.True);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(SignedExecutionPayloadEnvelope.Encode(read!), Is.EqualTo(SignedExecutionPayloadEnvelope.Encode(envelope)));
        Assert.That(reopened.TryGetExecutionPayloadEnvelope(Keccak.Compute("missing"), out _), Is.False);
        Assert.That(reopened.IsExecutionPayloadValid(RootOf(envelope)), Is.EqualTo(valid), "legacy envelopes have no EL verdict");
    }

    [Test]
    public void Valid_verdict_without_a_stored_envelope_creates_no_keys([Values] bool pruned)
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope envelope = Envelope(FirstGloasSlot + 1);
        ulong pastEnvelope = Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 2;
        if (pruned)
        {
            store.PutExecutionPayloadEnvelope(RootOf(envelope), envelope, valid: true);
            Prune(store, pastEnvelope, ulong.MaxValue);
        }

        store.SetExecutionPayloadValid(RootOf(envelope));
        BeaconChainStore reopened = new(db, Sepolia);
        Prune(reopened, pastEnvelope, ulong.MaxValue);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reopened.IsExecutionPayloadValid(RootOf(envelope)), Is.False, "late verdicts cannot outlive their envelope");
        Assert.That(db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes).GetAllKeys(), Is.Empty,
            "a missing envelope has no slot index to prune an orphan marker");
    }

    [Test]
    public void Prune_removes_only_envelopes_below_the_retention_window_and_the_finalized_block([Values] bool finalityLags)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        MemDb column = (MemDb)db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        ulong currentEpoch = Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 4 * BeaconChainStore.EnvelopePruneBatchSlots / SlotsPerEpoch;
        ulong windowStart = (currentEpoch - BeaconChainStore.MinEpochsForBlockRequests) * SlotsPerEpoch;
        ulong finalizedSlot = finalityLags ? windowStart - 1 : currentEpoch * SlotsPerEpoch;
        // Stored highest first, so the lowest slot is known only once a later store lowers the bound; the lowest is batches below the rest.
        SignedExecutionPayloadEnvelope[] kept = [Envelope(windowStart + 1), Envelope(windowStart)];
        SignedExecutionPayloadEnvelope[] belowWindow = [Envelope(windowStart - 1), Envelope(windowStart - 1, salt: 1)];
        SignedExecutionPayloadEnvelope[] pruned = [Envelope(windowStart - 3 * BeaconChainStore.EnvelopePruneBatchSlots - 5)];
        foreach (SignedExecutionPayloadEnvelope envelope in kept.Concat(belowWindow).Concat(pruned))
        {
            store.PutExecutionPayloadEnvelope(RootOf(envelope), envelope);
        }

        Prune(store, currentEpoch, finalizedSlot);
        long readsBefore = column.ReadsCount;
        Prune(store, currentEpoch, finalizedSlot);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(column.ReadsCount - readsBefore, Is.EqualTo(1), "a repeat prune reads the slot bounds and no pruned slot again");
        Assert.That(kept.Select(e => store.TryGetExecutionPayloadEnvelope(RootOf(e), out _)), Is.All.True, "at or above the window start");
        Assert.That(belowWindow.Select(e => store.TryGetExecutionPayloadEnvelope(RootOf(e), out _)), Is.All.EqualTo(finalityLags),
            "below the window start, both forks of one slot, kept only from the finalized block on");
        Assert.That(pruned.Select(e => store.TryGetExecutionPayloadEnvelope(RootOf(e), out _)), Is.All.False, "batches below both");
    }

    [Test]
    public void Prune_keeps_everything_until_the_window_leaves_the_fork_and_starts_afresh_once_past_every_envelope()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        SignedExecutionPayloadEnvelope[] first = [Envelope(FirstGloasSlot), Envelope(FirstGloasSlot + SlotsPerEpoch + 1)];
        foreach (SignedExecutionPayloadEnvelope envelope in first)
        {
            store.PutExecutionPayloadEnvelope(RootOf(envelope), envelope);
        }

        Prune(store, 0, ulong.MaxValue);
        Prune(store, Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests, ulong.MaxValue);
        bool[] keptAtTheFork = [.. first.Select(e => store.TryGetExecutionPayloadEnvelope(RootOf(e), out _))];

        ulong pastEverything = Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 2;
        Prune(store, pastEverything, ulong.MaxValue);
        bool[] keptPastEverything = [.. first.Select(e => store.TryGetExecutionPayloadEnvelope(RootOf(e), out _))];
        SignedExecutionPayloadEnvelope later = Envelope(FirstGloasSlot + 2 * SlotsPerEpoch + 1);
        store.PutExecutionPayloadEnvelope(RootOf(later), later);
        // The window's start slot saturates rather than wrapping to a slot below the envelope.
        Prune(store, (1UL << 59) + BeaconChainStore.MinEpochsForBlockRequests, ulong.MaxValue);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(keptAtTheFork, Is.All.True, "a window that has not left the fork epoch, or a current epoch younger than the window");
        Assert.That(keptPastEverything, Is.All.False, "the window starts above every stored slot, the highest included");
        Assert.That(store.TryGetExecutionPayloadEnvelope(RootOf(later), out _), Is.False, "an envelope stored after the store was emptied is still indexed for pruning");
    }

    [Test]
    public void Prune_keeps_a_finalized_envelope_at_the_highest_slot_and_leaves_no_key_once_past_every_envelope()
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        MemDb column = (MemDb)db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope below = Envelope(FirstGloasSlot + 1);
        SignedExecutionPayloadEnvelope finalized = Envelope(FirstGloasSlot + 6);
        store.PutExecutionPayloadEnvelope(RootOf(below), below, valid: true);
        store.PutExecutionPayloadEnvelope(RootOf(finalized), finalized, valid: true);
        ulong windowAboveBoth = Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 10;

        Prune(store, windowAboveBoth, FirstGloasSlot + 6);
        bool belowKept = store.TryGetExecutionPayloadEnvelope(RootOf(below), out _);
        bool finalizedKept = store.TryGetExecutionPayloadEnvelope(RootOf(finalized), out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.IsExecutionPayloadValid(RootOf(finalized)), Is.True, "retaining the envelope retains its verdict");
            Assert.That(store.IsExecutionPayloadValid(RootOf(below)), Is.False, "pruning an envelope removes its verdict");
        }
        Prune(store, windowAboveBoth, ulong.MaxValue);
        int keysLeft = column.Count;
        long readsBefore = column.ReadsCount;
        Prune(store, windowAboveBoth + 1, ulong.MaxValue);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(belowKept, Is.False, "below the finalized block and the window");
        Assert.That(finalizedKept, Is.True, "the finalized block is the highest stored slot");
        Assert.That(keysLeft, Is.Zero, "no record, slot index entry or slot bounds survive pruning everything");
        Assert.That(column.ReadsCount - readsBefore, Is.EqualTo(1), "a prune of an emptied store reads the slot bounds only");
    }

    [Test]
    public void Slot_index_holds_each_root_once_and_whole([Values] bool corruptEntry)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        MemDb column = (MemDb)db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope first = Envelope(FirstGloasSlot + 1);
        SignedExecutionPayloadEnvelope second = Envelope(FirstGloasSlot + 1, salt: 1);
        byte[] slotKey = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(slotKey, FirstGloasSlot + 1);
        for (int i = 0; i < 3; i++)
        {
            store.PutExecutionPayloadEnvelope(RootOf(first), first);
        }

        int afterRepeats = column[slotKey]!.Length;
        if (corruptEntry)
        {
            column[slotKey] = [.. column[slotKey]!, 0xAB];
        }

        store.PutExecutionPayloadEnvelope(RootOf(second), second);
        int afterSecond = column[slotKey]!.Length;
        Prune(store, Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 2, ulong.MaxValue);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(afterRepeats, Is.EqualTo(Hash256.Size), "a repeated store of one envelope does not grow its slot entry");
        Assert.That(afterSecond, Is.EqualTo(2 * Hash256.Size), "a second envelope at the slot is appended whole, after any partial root");
        Assert.That(column.Count, Is.Zero, "every record the slot entry names is pruned");
    }

    [Test]
    public void Prune_without_a_spec_is_refused()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());

        Assert.Throws<InvalidOperationException>(() => store.PruneExecutionPayloadEnvelopes(Sepolia.GloasForkEpoch, ulong.MaxValue));
    }

    public enum Corruption
    {
        NotSnappy,
        NotAnEnvelope,
        Truncated,
        NamesAnotherBlock,
        LengthNear2GiB,
        LengthAboveInt32,
        Length128MiB,
    }

    [Test]
    public void Corrupt_stored_envelope_is_refused([Values] Corruption corruption)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope envelope = Envelope(FirstGloasSlot + 1);
        Hash256 root = RootOf(envelope);
        store.PutExecutionPayloadEnvelope(root, envelope);
        byte[] ssz = SignedExecutionPayloadEnvelope.Encode(envelope);

        db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes)[root.Bytes] = CorruptRecord(corruption, ssz);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => store.TryGetExecutionPayloadEnvelope(root, out _));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, Is.LessThan(1 << 20), "a corrupt length header is not allocated");
    }

    /// <remarks>The length cases are snappy length headers with no data after them.</remarks>
    internal static byte[] CorruptRecord(Corruption corruption, byte[] ssz) => corruption switch
    {
        Corruption.NotSnappy => [0xFF],
        Corruption.NotAnEnvelope => Snappy.CompressToArray([1, 2, 3]),
        Corruption.Truncated => Snappy.CompressToArray(ssz.AsSpan(0, ssz.Length - 1)),
        Corruption.NamesAnotherBlock => Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(Envelope(FirstGloasSlot + 1, salt: 1))),
        Corruption.LengthNear2GiB => [0xF0, 0xFF, 0xFF, 0xFF, 0x07],
        Corruption.LengthAboveInt32 => [0xFF, 0xFF, 0xFF, 0xFF, 0x0F],
        Corruption.Length128MiB => [0x80, 0x80, 0x80, 0x40],
        _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
    };

    /// <summary>Prunes on a background thread, so a prune that never returns fails the test rather than stalling the run.</summary>
    private static void Prune(BeaconChainStore store, ulong currentEpoch, ulong finalizedSlot)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try
            {
                store.PruneExecutionPayloadEnvelopes(currentEpoch, finalizedSlot);
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        { IsBackground = true };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a prune returns");
        if (error is not null) ExceptionDispatchInfo.Throw(error);
    }

    private static Hash256 RootOf(SignedExecutionPayloadEnvelope envelope) => envelope.Message!.BeaconBlockRoot!;

    /// <param name="salt">Tells apart two envelopes at one slot.</param>
    internal static SignedExecutionPayloadEnvelope Envelope(ulong slot, int salt = 0) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = slot, BlockHash = Keccak.Compute($"payload {slot} {salt}"), Withdrawals = [] },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = Presets.BuilderIndexSelfBuild,
            BeaconBlockRoot = Keccak.Compute($"block {slot} {salt}"),
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };
}
