// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Storage.BeaconChainStoreEnvelopeTests;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Storage;

[HardTimeout(60_000)]
public class BeaconChainStoreEnvelopeRetentionTests
{
    private const ulong ForkSlot = 32;

    private static readonly byte[] EnvelopeBoundsKey = [0];

    public enum FinalizedCheckpoint
    {
        StateRetained,
        StateNotRetained,
        UnknownWithNoAnchor,
    }

    /// <summary>
    /// gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange/ByRoot serve
    /// <c>[max(GLOAS_FORK_EPOCH, current_epoch - compute_min_epochs_for_block_requests()), current_epoch]</c>, and replay after a restart
    /// starts from the persisted anchor, so finalization prunes only below both. With no anchor, a checkpoint block fork choice does not
    /// hold may sit below its epoch's start slot, so nothing outside the window is known to be safe to prune.
    /// </summary>
    [Test]
    public void Finalization_prunes_envelopes_below_the_serve_window_and_the_persisted_anchor([Values] FinalizedCheckpoint checkpoint)
    {
        bool finalizedStateRetained = checkpoint == FinalizedCheckpoint.StateRetained;
        SignedGloasChain chain = new();
        BeaconChainStore store = chain.CreateStore();
        ulong secondsPerSlot = chain.Spec.SecondsPerSlot;
        DateTime genesis = DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime);
        ManualTimestamper timestamper = new(genesis.AddSeconds((ForkSlot + 3) * secondsPerSlot));
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), store: store, clock: new SlotClock(chain.Spec, timestamper));
        if (checkpoint != FinalizedCheckpoint.UnknownWithNoAnchor)
        {
            store.SetAnchor(chain.AnchorRoot, chain.AnchorBlock.Message!.Slot);
        }

        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block finalized = chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        foreach (SignedGloasChain.Block block in (SignedGloasChain.Block[])[first, finalized])
        {
            Assert.That(importer.Import(block.Forked, block.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported), "fixture");
            store.PutExecutionPayloadEnvelope(block.Root, block.Envelope);
        }

        ulong currentEpoch = chain.Spec.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 10;
        ulong windowStart = (currentEpoch - BeaconChainStore.MinEpochsForBlockRequests) * chain.Spec.SlotsPerEpoch;
        SignedExecutionPayloadEnvelope inWindow = Envelope(windowStart);
        store.PutExecutionPayloadEnvelope(inWindow.Message!.BeaconBlockRoot!, inWindow);
        timestamper.UtcNow = genesis.AddSeconds(currentEpoch * chain.Spec.SlotsPerEpoch * secondsPerSlot);

        // A checkpoint whose state is not retained leaves the persisted anchor where it was.
        importer.OnFinalized(finalizedStateRetained ? new CheckpointRef(2, finalized.Root) : new CheckpointRef(2, Keccak.Compute("state not retained")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetAnchor(out _, out ulong anchorSlot), Is.EqualTo(checkpoint != FinalizedCheckpoint.UnknownWithNoAnchor));
            if (checkpoint != FinalizedCheckpoint.UnknownWithNoAnchor)
            {
                Assert.That(anchorSlot, Is.EqualTo(finalizedStateRetained ? ForkSlot + 2 : chain.AnchorBlock.Message!.Slot), "fixture: where replay starts after a restart");
            }

            Assert.That(store.TryGetExecutionPayloadEnvelope(first.Root, out _), Is.EqualTo(!finalizedStateRetained), "below the window, kept only while replay starts below it");
            Assert.That(store.TryGetExecutionPayloadEnvelope(finalized.Root, out _), Is.True, "the finalized block's envelope is below the window but replay needs it");
            Assert.That(store.TryGetExecutionPayloadEnvelope(inWindow.Message!.BeaconBlockRoot!, out _), Is.True, "in the window");
        }
    }

    /// <summary>phase0/p2p-interface.md <c>MAX_PAYLOAD_SIZE</c>: every read refuses a longer record, so none is written.</summary>
    [Test]
    public void Put_stores_an_envelope_of_max_payload_size_and_refuses_one_byte_more([Values(0, 1)] int overLimit)
    {
        const int maxPayloadSize = 10 * 1024 * 1024;
        MemColumnsDb<BeaconChainDbColumns> db = new();
        IDb column = db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope envelope = Envelope(FirstGloasSlot + 1);
        envelope.Message!.Payload!.Transactions = [new TransactionGloas { Bytes = [] }];
        int emptyTransactionLength = SignedExecutionPayloadEnvelope.Encode(envelope).Length;
        envelope.Message.Payload.Transactions[0].Bytes = new byte[maxPayloadSize + overLimit - emptyTransactionLength];
        Hash256 root = envelope.Message.BeaconBlockRoot!;
        Assert.That(SignedExecutionPayloadEnvelope.Encode(envelope), Has.Length.EqualTo(maxPayloadSize + overLimit), "fixture");

        if (overLimit == 0)
        {
            store.PutExecutionPayloadEnvelope(root, envelope);
            Assert.That(store.TryGetExecutionPayloadEnvelope(root, out SignedExecutionPayloadEnvelope? read), Is.True);
            Assert.That(read!.Message!.Payload!.Transactions![0].Bytes, Has.Length.EqualTo(envelope.Message.Payload.Transactions[0].Bytes!.Length));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => store.PutExecutionPayloadEnvelope(root, envelope));
            Assert.That(column.GetAllKeys(), Is.Empty, "a refused envelope writes no record, slot index entry or slot bounds");
        }
    }

    public enum BoundsDamage
    {
        TooShort,
        TooLong,
        Inverted,
    }

    /// <summary>
    /// An unusable slot bounds record is rebuilt from the slot index and written back, so no stored envelope escapes a later prune
    /// and a store inside the rebuilt bounds does not leave the damaged record to be rebuilt again.
    /// </summary>
    [Test]
    public void Damaged_slot_bounds_are_rebuilt_so_every_envelope_is_still_pruned([Values] BoundsDamage damage, [Values] bool storeAfterDamage)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        IDb column = db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope[] envelopes = [Envelope(FirstGloasSlot + 1), Envelope(FirstGloasSlot + 5), Envelope(FirstGloasSlot + 9)];
        store.PutExecutionPayloadEnvelope(envelopes[0].Message!.BeaconBlockRoot!, envelopes[0]);
        store.PutExecutionPayloadEnvelope(envelopes[2].Message!.BeaconBlockRoot!, envelopes[2]);
        column[EnvelopeBoundsKey] = DamagedBounds(damage);

        if (storeAfterDamage)
        {
            store.PutExecutionPayloadEnvelope(envelopes[1].Message!.BeaconBlockRoot!, envelopes[1]);
            Assert.That(column[EnvelopeBoundsKey], Is.EqualTo(Bounds(FirstGloasSlot + 1, FirstGloasSlot + 9)), "the rebuilt bounds cover the slots stored before the damage");
        }

        store.PruneExecutionPayloadEnvelopes(Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 2, ulong.MaxValue);

        Assert.That(column.GetAllKeys(), Is.Empty, "no record, slot index entry or slot bounds survive pruning past every envelope");
    }

    /// <summary>A damaged slot bounds record over an empty slot index is removed, so later prunes do not scan the column for it again.</summary>
    [Test]
    public void Damaged_slot_bounds_with_no_stored_envelope_are_removed([Values] BoundsDamage damage)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        IDb column = db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        column[EnvelopeBoundsKey] = DamagedBounds(damage);

        store.PruneExecutionPayloadEnvelopes(Sepolia.GloasForkEpoch + BeaconChainStore.MinEpochsForBlockRequests + 2, ulong.MaxValue);

        Assert.That(column.GetAllKeys(), Is.Empty);
    }

    /// <summary>
    /// A database last opened by a build that had the envelope column at an older schema version is restamped without the children
    /// index rebuild; a newer version is refused, as <c>A_database_from_a_newer_schema_version_is_refused_and_left_unstamped</c> shows.
    /// </summary>
    [Test]
    public void Database_at_the_children_index_version_upgrades_without_a_rebuild_and_keeps_its_envelopes()
    {
        const uint childrenIndexVersion = 2;
        MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, Sepolia);
        Hash256 blockRoot = Keccak.Compute("block");
        store.PutForkedBlock(blockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(FirstGloasSlot + 1)));
        Hash256 unindexedRoot = Keccak.Compute("block stored without the children index");
        byte[] unindexed = SignedBeaconBlockCodec.Encode(new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(FirstGloasSlot + 2)), Sepolia);
        db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(unindexedRoot.Bytes, Snappy.CompressToArray(unindexed));
        SignedExecutionPayloadEnvelope envelope = Envelope(FirstGloasSlot + 1);
        store.PutExecutionPayloadEnvelope(envelope.Message!.BeaconBlockRoot!, envelope);
        store.SetSchemaVersion(childrenIndexVersion);

        BeaconChainStore reopened = new(db, Sepolia);
        reopened.EnsureSchemaVersion();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.TryGetSchemaVersion(out uint version), Is.True);
            Assert.That(version, Is.EqualTo(BeaconChainStore.CurrentSchemaVersion).And.GreaterThan(childrenIndexVersion), "a build that stamps the children index version must refuse a database this build wrote");
            Assert.That(reopened.TryGetChildren(unindexedRoot, out _, out _), Is.False, "the children index is not rebuilt");
            Assert.That(reopened.TryGetExecutionPayloadEnvelope(envelope.Message.BeaconBlockRoot!, out _), Is.True);
            Assert.That(reopened.HasBlock(blockRoot), Is.True);
        }
    }

    private static byte[] DamagedBounds(BoundsDamage damage) => damage switch
    {
        BoundsDamage.TooShort => [1, 2, 3],
        BoundsDamage.TooLong => new byte[3 * sizeof(ulong)],
        BoundsDamage.Inverted => Bounds(FirstGloasSlot + 9, FirstGloasSlot + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(damage)),
    };

    /// <summary>Encodes the inclusive stored slot bounds.</summary>
    internal static byte[] Bounds(ulong lowest, ulong highest)
    {
        byte[] value = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, lowest);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(sizeof(ulong)), highest);
        return value;
    }
}
