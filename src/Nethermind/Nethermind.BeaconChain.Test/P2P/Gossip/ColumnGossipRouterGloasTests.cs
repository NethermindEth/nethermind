// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// A Gloas sidecar is the one gossip message this node can fully validate without state, so it must be Accepted
// (gloas/p2p-interface.md: a valid sidecar MUST be re-broadcast); REJECT follows the spec order, and a sidecar
// whose block is not held yet must be parked, because its message id is never redelivered once dropped.
public class ColumnGossipRouterGloasTests
{
    private const ulong Column = 5;
    private static readonly ulong BlockSlot = FirstGloasSlot + 1;
    private static readonly Hash256 UnknownRoot = new(Enumerable.Repeat((byte)0xEE, 32).ToArray());
    private static readonly SignedBeaconBlockGloas Block = BlockWithBlobs();
    private static readonly Hash256 BlockRoot = SszRoots.HashTreeRoot(Block.Message!);
    private static readonly SignedBeaconBlock FuluBlock = CreateMinimalBlock(FirstGloasSlot - 1);
    private static readonly Hash256 FuluBlockRoot = SszRoots.HashTreeRoot(FuluBlock.Message!);

    public enum Outcome
    {
        Stored,
        Parked,
        Neither,
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case("valid sidecar is stored and accepted", Sidecar(), Column, MessageValidity.Accepted, null, Outcome.Stored);
        yield return Case("wrong subnet", Sidecar(), Column + 1, MessageValidity.Rejected, ColumnGossipDropReason.WrongSubnet, Outcome.Neither);
        yield return Case("far future slot", Sidecar(slot: BlockSlot + 2), Column, MessageValidity.Ignored, ColumnGossipDropReason.FutureSlot, Outcome.Neither);
        yield return Case("next slot before the clock disparity is parked", Sidecar(slot: BlockSlot + 1, root: UnknownRoot), Column, MessageValidity.Ignored, ColumnGossipDropReason.FutureSlot, Outcome.Parked);
        yield return Case("unknown block is parked", Sidecar(root: UnknownRoot), Column, MessageValidity.Ignored, ColumnGossipDropReason.UnknownBlock, Outcome.Parked);
        yield return Case("unknown block at an earlier slot is not parked", Sidecar(slot: BlockSlot - 1, root: UnknownRoot), Column, MessageValidity.Ignored, ColumnGossipDropReason.UnknownBlock, Outcome.Neither);
        yield return Case("unknown block with out of range index is not parked", Sidecar(index: Column + Eip7594DasConstants.DataColumnSidecarSubnetCount, root: UnknownRoot), Column, MessageValidity.Ignored, ColumnGossipDropReason.FailedStructure, Outcome.Neither);
        yield return Case("unknown block with a proof missing is not parked", Sidecar(root: UnknownRoot, mutate: static s => s.KzgProofs = s.KzgProofs![..1]), Column, MessageValidity.Ignored, ColumnGossipDropReason.FailedStructure, Outcome.Neither);
        yield return Case("pre-Gloas block root", Sidecar(root: FuluBlockRoot), Column, MessageValidity.Rejected, ColumnGossipDropReason.SlotMismatch, Outcome.Neither);
        yield return Case("slot differs from the block", Sidecar(slot: BlockSlot - 1), Column, MessageValidity.Rejected, ColumnGossipDropReason.SlotMismatch, Outcome.Neither);
        yield return Case("fewer cells than the bid commits", Sidecar(mutate: static s => { s.Column = s.Column![..1]; s.KzgProofs = s.KzgProofs![..1]; }), Column, MessageValidity.Rejected, ColumnGossipDropReason.FailedStructure, Outcome.Neither);
        yield return Case("swapped KZG proofs", Sidecar(mutate: static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]]), Column, MessageValidity.Rejected, ColumnGossipDropReason.FailedKzgProofs, Outcome.Neither);
    }

    [TestCaseSource(nameof(Cases))]
    public void Gloas_sidecar_verdict_follows_the_spec_order(DataColumnSidecarGloas sidecar, ulong subnet, MessageValidity expected, ColumnGossipDropReason? reason, Outcome outcome)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(subscribed: [Column, Column + 1]);

        MessageValidity validity = router.Handle(subnet, gloasTopic: true, Encode(sidecar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected));
            if (reason is { } dropReason)
            {
                Assert.That(router.GetDropCount(dropReason), Is.EqualTo(1), "the drop is counted under its reason");
            }

            Hash256 root = sidecar.BeaconBlockRoot!;
            Assert.That(pool.TryGetGloas(root, sidecar.Index, out _), Is.EqualTo(outcome == Outcome.Stored), "stored as verified");
            Assert.That(pool.GetPendingGloas(root, sidecar.Index), Has.Length.EqualTo(outcome == Outcome.Parked ? 1 : 0), "parked as a pending candidate");
        }
    }

    private static IEnumerable<TestCaseData> UndecodableCases()
    {
        yield return new TestCaseData(Snappy.CompressToArray([1, 2, 3]), ColumnGossipDropReason.InvalidSsz).SetName("invalid SSZ");

        // One cell past the largest scheduled max_blobs_per_block is over compute_max_data_column_sidecar_size.
        int overLimit = (int)(DataColumnSidecarGloasSize.ComputeMax(Sepolia) - DataColumnSidecarGloasSize.FixedPartLength) / DataColumnSidecarGloasSize.BytesPerBlob + 1;
        yield return new TestCaseData(Encode(Sidecar(root: UnknownRoot, mutate: s => Widen(s, overLimit))), ColumnGossipDropReason.Oversized).SetName("over the computed size bound");
    }

    [TestCaseSource(nameof(UndecodableCases))]
    public void Undecodable_or_oversized_message_is_rejected_before_the_pool(byte[] message, ColumnGossipDropReason reason)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();

        MessageValidity validity = router.Handle(Column, gloasTopic: true, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(router.GetDropCount(reason), Is.EqualTo(1));
            Assert.That(pool.GetPendingGloas(UnknownRoot, Column), Is.Empty, "nothing reaches the pending candidates");
        }
    }

    [Test]
    public void Column_longer_than_its_epochs_blob_limit_is_not_parked()
    {
        // Mainnet schedules 15 blobs from epoch 412672 and 21 from 419072, so 16 cells at the first fit the size bound but not the blob limit.
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        ulong wallSlot = 419_072 * mainnet.SlotsPerEpoch;
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(mainnet, wallSlot);
        DataColumnSidecarGloas sidecar = Sidecar(slot: 412_700 * mainnet.SlotsPerEpoch, root: UnknownRoot, mutate: static s => Widen(s, 16));

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Encode(sidecar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedBlobCount), Is.EqualTo(1));
            Assert.That(pool.GetPendingGloas(UnknownRoot, Column), Is.Empty);
        }
    }

    [Test]
    public void Forgery_for_an_unknown_block_does_not_displace_another_candidate()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();
        DataColumnSidecarGloas genuine = Sidecar(root: UnknownRoot);
        DataColumnSidecarGloas forged = Sidecar(root: UnknownRoot, mutate: static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]]);

        router.Handle(Column, gloasTopic: true, Encode(genuine));
        router.Handle(Column, gloasTopic: true, Encode(forged));

        Assert.That(pool.GetPendingGloas(UnknownRoot, Column).Select(static s => s.KzgProofs![0].AsSpan().ToArray()),
            Is.EquivalentTo(new[] { genuine.KzgProofs![0].AsSpan().ToArray(), forged.KzgProofs![0].AsSpan().ToArray() }),
            "both candidates are kept, so availability can still find the genuine one");
    }

    [Test]
    public void Second_copy_of_an_accepted_sidecar_is_ignored_as_a_duplicate()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool _) = Create();
        byte[] message = Encode(Sidecar());

        MessageValidity first = router.Handle(Column, gloasTopic: true, message);
        MessageValidity second = router.Handle(Column, gloasTopic: true, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, second), Is.EqualTo((MessageValidity.Accepted, MessageValidity.Ignored)));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(1));
        }
    }

    [Test]
    public void Sidecar_on_a_subnet_this_node_did_not_subscribe_is_ignored()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(subscribed: [Column + 1]);

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.UnsubscribedSubnet), Is.EqualTo(1));
            Assert.That(pool.TryGetGloas(BlockRoot, Column, out _), Is.False);
        }
    }

    [Test]
    public void Accepted_sidecar_raised_again_on_its_topic_is_processed_once()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool, ForwardingTopic topic) = CreateWithTopic();
        byte[] message = Encode(Sidecar());

        MessageValidity validity = router.Handle(Column, gloasTopic: true, message);
        // The pinned pubsub library raises the topic's OnMessage for every message its validator Accepts.
        topic.Deliver(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Accepted));
            Assert.That(pool.TryGetGloas(BlockRoot, Column, out _), Is.True);
            Assert.That(Enum.GetValues<ColumnGossipDropReason>().Sum(router.GetDropCount), Is.Zero, "a second pass would count the sidecar as a duplicate");
        }
    }

    /// <summary>The spec orders verify_data_column_sidecar's REJECT after the block checks, so only a block already read can convict.</summary>
    [Test]
    public void Out_of_range_index_on_its_subnet_is_rejected_only_once_its_block_is_read([Values] bool blockRead)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();
        if (blockRead)
        {
            router.Handle(Column, gloasTopic: true, Encode(Sidecar()));
        }

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Encode(Sidecar(index: Column + Eip7594DasConstants.DataColumnSidecarSubnetCount)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(blockRead ? MessageValidity.Rejected : MessageValidity.Ignored));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedStructure), Is.EqualTo(1));
            Assert.That(pool.GetPendingGloas(BlockRoot, Column + Eip7594DasConstants.DataColumnSidecarSubnetCount), Is.Empty);
        }
    }

    /// <summary>Stored pre-Gloas roots are public, so repeating one must not repeat a block decode.</summary>
    [TestCase(true, 1, TestName = "Sidecars naming a stored pre-Gloas block read it from the store once")]
    [TestCase(false, 0, TestName = "Sidecar no bid can match is dropped without a store read")]
    public void Sidecars_naming_a_stored_pre_gloas_block_read_the_store_at_most_once(bool wellFormed, long expectedReads)
    {
        MemColumnsDb<BeaconChainDbColumns>? columns = null;
        (ColumnGossipRouter router, DataColumnSidecarPool _) = Create(populate: (db, _) => columns = db);
        MemDb blocks = (MemDb)columns!.GetColumnDb(BeaconChainDbColumns.Blocks);
        long readsBefore = blocks.ReadsCount;

        for (int i = 0; i < 3; i++)
        {
            int variant = i;
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: FuluBlockRoot, mutate: s =>
            {
                s.KzgProofs![0] = DistinctProof(variant);
                if (!wellFormed) s.Column = [];
            })));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.ReadsCount - readsBefore, Is.EqualTo(expectedReads));
            Assert.That(router.GetDropCount(wellFormed ? ColumnGossipDropReason.SlotMismatch : ColumnGossipDropReason.FailedStructure), Is.EqualTo(3));
        }
    }

    public enum StoredRoots
    {
        FuluAtGloasSlot,
        FuluBeforeFork,
        GloasAtOtherSlots,
    }

    /// <summary>
    /// Stored roots are public and outnumber any cache, and the pinned pubsub library does not penalise a REJECT, so a
    /// peer cycling them must cost at most the slot's decode budget; a sidecar before the fork costs no decode at all.
    /// </summary>
    [TestCase(StoredRoots.FuluAtGloasSlot, ColumnGossipRouter.NonGloasBlockCacheSize + 1, ColumnGossipRouter.StoreDecodesPerSlot)]
    [TestCase(StoredRoots.FuluBeforeFork, ColumnGossipRouter.NonGloasBlockCacheSize + 1, 0)]
    [TestCase(StoredRoots.GloasAtOtherSlots, ColumnGossipRouter.GloasBlockCacheSize + 1, ColumnGossipRouter.StoreDecodesPerSlot)]
    public void Sidecars_cycling_more_stored_roots_than_the_cache_holds_decode_at_most_the_slot_budget(StoredRoots kind, int count, long expectedReads)
    {
        Hash256[] roots = [];
        MemDb? blocks = null;
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(populate: (db, store) =>
        {
            blocks = (MemDb)db.GetColumnDb(BeaconChainDbColumns.Blocks);
            roots = kind == StoredRoots.GloasAtOtherSlots ? StoreGloasBlocksAfterBlockSlot(store, count) : StoreFuluBlocks(store, count);
        });
        long readsBefore = blocks!.ReadsCount;
        ulong slot = kind == StoredRoots.FuluBeforeFork ? FirstGloasSlot - 1 : BlockSlot;

        List<MessageValidity> verdicts = [];
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (Hash256 root in roots)
            {
                verdicts.Add(router.Handle(Column, gloasTopic: true, Encode(Sidecar(slot: slot, root: root))));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.ReadsCount - readsBefore, Is.EqualTo(expectedReads));
            Assert.That(verdicts, Has.None.EqualTo(MessageValidity.Accepted));
            Assert.That(pool.PendingGloasCount, Is.Zero);
        }
    }

    /// <summary>A peer spending the decode budget must not stop sidecars of a block this node already decoded from being accepted.</summary>
    [Test]
    public void Sidecar_of_a_cached_block_is_accepted_after_the_decode_budget_is_spent()
    {
        Hash256[] roots = [];
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(populate: (_, store) => roots = StoreFuluBlocks(store, ColumnGossipRouter.StoreDecodesPerSlot));
        // A forgery reads and caches the block without marking its (root, index) seen.
        MessageValidity forged = router.Handle(Column, gloasTopic: true, Encode(Sidecar(mutate: static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]])));
        foreach (Hash256 root in roots)
        {
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: root)));
        }

        MessageValidity genuine = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forged, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.StoreDecodeBudgetSpent), Is.GreaterThan(0), "the budget is spent");
            Assert.That(genuine, Is.EqualTo(MessageValidity.Accepted));
            Assert.That(pool.TryGetGloas(BlockRoot, Column, out _), Is.True);
        }
    }

    /// <summary>The budget bounds decode work per slot, so an honest block read after it is spent must be accepted in the next slot.</summary>
    [Test]
    public void Spent_decode_budget_is_restored_at_the_next_slot()
    {
        Hash256[] roots = [];
        MemDb? blocks = null;
        ManualTimestamper timestamper = WallClock(Sepolia, BlockSlot);
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(timestamper: timestamper, populate: (db, store) =>
        {
            blocks = (MemDb)db.GetColumnDb(BeaconChainDbColumns.Blocks);
            roots = StoreFuluBlocks(store, ColumnGossipRouter.StoreDecodesPerSlot);
        });
        foreach (Hash256 root in roots)
        {
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: root)));
        }

        long readsBefore = blocks!.ReadsCount;
        MessageValidity spent = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));
        long readsWhileSpent = blocks.ReadsCount - readsBefore;
        timestamper.Add(TimeSpan.FromSeconds(Sepolia.SecondsPerSlot));
        MessageValidity nextSlot = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spent, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(readsWhileSpent, Is.Zero, "a spent budget reads no block record");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.StoreDecodeBudgetSpent), Is.EqualTo(1));
            Assert.That(pool.GetPendingGloas(BlockRoot, Column), Is.Empty, "a stored block is never parked for");
            Assert.That(nextSlot, Is.EqualTo(MessageValidity.Accepted));
        }
    }

    /// <summary>
    /// A peer can spend the decode budget for free, so the sidecars of the canonical block at a recent slot must still be
    /// accepted, and an early sidecar of a block not yet stored must still be parked.
    /// </summary>
    [TestCase(true, 0UL, MessageValidity.Accepted, TestName = "Canonical block at the current slot is decoded after the budget is spent")]
    [TestCase(true, 1UL, MessageValidity.Accepted, TestName = "Canonical block at the previous slot is decoded after the budget is spent")]
    [TestCase(true, 2UL, MessageValidity.Ignored, TestName = "Canonical block at an older slot waits for the next slot's budget")]
    [TestCase(false, 0UL, MessageValidity.Ignored, TestName = "Competing block at the current slot waits for the next slot's budget")]
    public void Spent_decode_budget_still_serves_the_canonical_block_and_parks_unknown_blocks(bool canonical, ulong slotsSinceBlock, MessageValidity expected)
    {
        Hash256[] roots = [];
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(wallSlot: BlockSlot + slotsSinceBlock, populate: (_, store) =>
        {
            roots = StoreFuluBlocks(store, ColumnGossipRouter.StoreDecodesPerSlot);
            store.SetCanonicalRoot(BlockSlot, canonical ? BlockRoot : Keccak.Compute("competing block"));
        });
        foreach (Hash256 root in roots)
        {
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: root)));
        }

        MessageValidity genuine = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));
        router.Handle(Column, gloasTopic: true, Encode(Sidecar(slot: BlockSlot + slotsSinceBlock, root: UnknownRoot)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(router.GetDropCount(ColumnGossipDropReason.SlotMismatch), Is.EqualTo(ColumnGossipRouter.StoreDecodesPerSlot), "the budget is spent");
            Assert.That(genuine, Is.EqualTo(expected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.StoreDecodeBudgetSpent), Is.EqualTo(expected == MessageValidity.Accepted ? 0 : 1));
            Assert.That(pool.GetPendingGloas(UnknownRoot, Column), Has.Length.EqualTo(1), "a block not yet stored costs no decode");
        }
    }

    /// <summary>The canonical block is the honest case, so decoding it must leave the whole budget for the blocks it does not cover.</summary>
    [Test]
    public void Decoding_the_canonical_block_does_not_spend_the_decode_budget()
    {
        Hash256[] roots = [];
        MemDb? blocks = null;
        (ColumnGossipRouter router, DataColumnSidecarPool _) = Create(populate: (db, store) =>
        {
            blocks = (MemDb)db.GetColumnDb(BeaconChainDbColumns.Blocks);
            roots = StoreFuluBlocks(store, ColumnGossipRouter.StoreDecodesPerSlot);
            store.SetCanonicalRoot(BlockSlot, BlockRoot);
        });
        long readsBefore = blocks!.ReadsCount;

        MessageValidity genuine = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));
        foreach (Hash256 root in roots)
        {
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: root)));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(genuine, Is.EqualTo(MessageValidity.Accepted));
            Assert.That(blocks.ReadsCount - readsBefore, Is.EqualTo(1 + ColumnGossipRouter.StoreDecodesPerSlot));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.StoreDecodeBudgetSpent), Is.Zero);
        }
    }

    /// <summary>A cached non-Gloas root costs no decode, so repeating one must not spend the budget a held block needs.</summary>
    [Test]
    public void Repeated_stored_pre_gloas_root_spends_one_decode_of_the_budget()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool _) = Create();
        for (int i = 0; i <= ColumnGossipRouter.StoreDecodesPerSlot; i++)
        {
            int variant = i;
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: FuluBlockRoot, mutate: s => s.KzgProofs![0] = DistinctProof(variant))));
        }

        MessageValidity genuine = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(router.GetDropCount(ColumnGossipDropReason.SlotMismatch), Is.EqualTo(ColumnGossipRouter.StoreDecodesPerSlot + 1));
            Assert.That(genuine, Is.EqualTo(MessageValidity.Accepted));
        }
    }

    /// <summary>Unknown roots cost the store a key lookup, not a decode, so a flood of them must not deny the decode of a held block.</summary>
    [Test]
    public void Sidecars_for_blocks_not_stored_do_not_spend_the_decode_budget()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();
        for (int i = 0; i <= ColumnGossipRouter.StoreDecodesPerSlot; i++)
        {
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: Keccak.Compute(BitConverter.GetBytes(i)))));
        }

        MessageValidity genuine = router.Handle(Column, gloasTopic: true, Encode(Sidecar()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.PendingGloasCount, Is.EqualTo(ColumnGossipRouter.StoreDecodesPerSlot + 1), "each unknown block is parked");
            Assert.That(genuine, Is.EqualTo(MessageValidity.Accepted));
        }
    }

    /// <summary>
    /// The validator is not told which peer sent a sidecar, so one peer can mint any number of distinct forgeries:
    /// they must neither push out a candidate that arrived earlier nor stack KZG work on one (root, column).
    /// </summary>
    [Test]
    public void Forged_flood_neither_evicts_an_earlier_candidate_nor_overfills_its_key([Values] bool sameKey)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();
        DataColumnSidecarGloas honest = Sidecar(root: UnknownRoot);
        router.Handle(Column, gloasTopic: true, Encode(honest));

        for (int i = 0; i <= DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            int variant = i;
            Hash256 root = sameKey ? UnknownRoot : Keccak.Compute(BitConverter.GetBytes(variant));
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: root, mutate: s => s.KzgProofs![0] = DistinctProof(variant))));
        }

        DataColumnSidecarGloas[] pending = pool.GetPendingGloas(UnknownRoot, Column);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pending.Select(static s => DataColumnSidecarGloas.Encode(s)), Has.Some.EqualTo(DataColumnSidecarGloas.Encode(honest)), "the earlier candidate stays");
            Assert.That(pending, Has.Length.LessThanOrEqualTo(DataColumnSidecarPool.MaxPendingGloasCandidatesPerKey));
            Assert.That(pool.PendingGloasCount, Is.LessThanOrEqualTo(DataColumnSidecarPool.MaxPendingGloasSidecars));
        }
    }

    [TestCase(true, MessageValidity.Rejected, MessageValidity.Accepted, TestName = "Forgery before the genuine sidecar of a held block does not suppress it")]
    [TestCase(false, MessageValidity.Accepted, MessageValidity.Ignored, TestName = "Forgery after the genuine sidecar of a held block is ignored as already seen")]
    public void Only_a_verified_sidecar_marks_its_root_and_index_seen(bool forgedFirst, MessageValidity firstExpected, MessageValidity secondExpected)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();
        byte[] genuine = Encode(Sidecar());
        byte[] forged = Encode(Sidecar(mutate: static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]]));

        MessageValidity first = router.Handle(Column, gloasTopic: true, forgedFirst ? forged : genuine);
        MessageValidity second = router.Handle(Column, gloasTopic: true, forgedFirst ? genuine : forged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, second), Is.EqualTo((firstExpected, secondExpected)));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(forgedFirst ? 0 : 1), "a seen (root, index) is ignored before its KZG check");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedKzgProofs), Is.EqualTo(forgedFirst ? 1 : 0));
            Assert.That(pool.TryGetGloas(BlockRoot, Column, out DataColumnSidecarGloas? stored) ? stored.KzgProofs![0].AsSpan().ToArray() : null,
                Is.EqualTo(Sidecar().KzgProofs![0].AsSpan().ToArray()), "the genuine sidecar is stored");
        }
    }

    [Test]
    public void Sidecar_at_the_size_and_blob_bounds_is_parked()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create();
        int maxBlobs = (int)Sepolia.GetBlobParameters(Sepolia.GetEpoch(BlockSlot))!.Value.MaxBlobsPerBlock;
        DataColumnSidecarGloas sidecar = Sidecar(root: UnknownRoot, mutate: s => Widen(s, maxBlobs));

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Encode(sidecar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DataColumnSidecarGloas.Encode(sidecar), Has.Length.EqualTo(44_072), "56 fixed bytes plus 21 cells and proofs of 2096 bytes");
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(pool.GetPendingGloas(UnknownRoot, Column), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public void Stored_bid_over_its_epochs_blob_limit_does_not_accept_a_matching_sidecar()
    {
        // A later schedule entry lifts the size bound to 22 cells while the block's own epoch allows 21.
        BeaconChainSpec spec = WithBlobEntry(Sepolia, new BlobScheduleEntry(Sepolia.GloasForkEpoch + 1, 22));
        SignedBeaconBlockGloas block = BlockWithBlobs();
        SszKzgCommitment commitment = DataColumnSidecarGloasTestFixture.Commitments()[0];
        block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = [.. Enumerable.Repeat(commitment, 22)];
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(spec, populate: (_, store) => store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block)));

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: root, mutate: static s => Widen(s, 22))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedBlobCount), Is.EqualTo(1));
            Assert.That(pool.TryGetGloas(root, Column, out _), Is.False);
        }
    }

    [TestCase(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, TestName = "Stored record that is not snappy is not treated as an unknown block")]
    [TestCase(new byte[] { 0x03, 0x08, 0x01, 0x02, 0x03 }, TestName = "Stored record that is not a block is not treated as an unknown block")]
    public void Unreadable_stored_block_ignores_the_sidecar_without_parking_it(byte[] record)
    {
        MemDb? blocks = null;
        (ColumnGossipRouter router, DataColumnSidecarPool pool) = Create(populate: (db, _) =>
        {
            blocks = (MemDb)db.GetColumnDb(BeaconChainDbColumns.Blocks);
            blocks.Set(UnknownRoot.Bytes, record);
        });
        long readsBefore = blocks!.ReadsCount;

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: UnknownRoot)));
        for (int i = 0; i < 2; i++)
        {
            int variant = i;
            router.Handle(Column, gloasTopic: true, Encode(Sidecar(root: UnknownRoot, mutate: s => s.KzgProofs![0] = DistinctProof(variant))));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(blocks.ReadsCount - readsBefore, Is.EqualTo(1), "an unreadable record is decoded once, not once per message");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.UnknownBlock), Is.EqualTo(3));
            Assert.That(pool.GetPendingGloas(UnknownRoot, Column), Is.Empty, "a candidate is kept only for a block this node may still receive");
        }
    }

    private static (ColumnGossipRouter Router, DataColumnSidecarPool Pool) Create(BeaconChainSpec? spec = null, ulong? wallSlot = null, ulong[]? subscribed = null,
        Action<MemColumnsDb<BeaconChainDbColumns>, BeaconChainStore>? populate = null, ManualTimestamper? timestamper = null)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = CreateWithTopic(spec, wallSlot, subscribed, populate, timestamper);
        return (router, pool);
    }

    private static (ColumnGossipRouter Router, DataColumnSidecarPool Pool, ForwardingTopic Topic) CreateWithTopic(BeaconChainSpec? spec = null, ulong? wallSlot = null, ulong[]? subscribed = null,
        Action<MemColumnsDb<BeaconChainDbColumns>, BeaconChainStore>? populate = null, ManualTimestamper? timestamper = null)
    {
        spec ??= Sepolia;
        ulong slot = wallSlot ?? BlockSlot;
        MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, spec);
        if (spec == Sepolia)
        {
            store.PutForkedBlock(BlockRoot, new ForkedSignedBeaconBlock.OfGloas(Block));
            store.PutForkedBlock(FuluBlockRoot, new ForkedSignedBeaconBlock.OfFulu(FuluBlock));
        }

        populate?.Invoke(db, store);

        DataColumnSidecarPool pool = new();
        SlotClock clock = new(spec, timestamper ?? WallClock(spec, slot));
        ColumnGossipRouter router = new(spec, clock, LimboLogs.Instance, pool, store);
        ForwardingTopic topic = new();
        router.Start(_ => topic, ForkDigest.Compute(spec, spec.GetEpoch(slot)), subscribed ?? [Column]);
        return (router, pool, topic);
    }

    private static ManualTimestamper WallClock(BeaconChainSpec spec, ulong slot) =>
        new(DateTime.UnixEpoch.AddSeconds(spec.GenesisTime + slot * spec.SecondsPerSlot + 6));

    private static Hash256[] StoreFuluBlocks(BeaconChainStore store, int count)
    {
        Hash256[] roots = new Hash256[count];
        for (int i = 0; i < count; i++)
        {
            SignedBeaconBlock fulu = CreateMinimalBlock(FirstGloasSlot - 2 - (ulong)i);
            roots[i] = SszRoots.HashTreeRoot(fulu.Message!);
            store.PutForkedBlock(roots[i], new ForkedSignedBeaconBlock.OfFulu(fulu));
        }

        return roots;
    }

    private static Hash256[] StoreGloasBlocksAfterBlockSlot(BeaconChainStore store, int count)
    {
        Hash256[] roots = new Hash256[count];
        for (int i = 0; i < count; i++)
        {
            SignedBeaconBlockGloas gloas = CreateMinimalGloasBlock(BlockSlot + 1 + (ulong)i);
            roots[i] = SszRoots.HashTreeRoot(gloas.Message!);
            store.PutForkedBlock(roots[i], new ForkedSignedBeaconBlock.OfGloas(gloas));
        }

        return roots;
    }

    private static SignedBeaconBlockGloas BlockWithBlobs()
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot + 1);
        block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        return block;
    }

    private static DataColumnSidecarGloas Sidecar(ulong index = Column, ulong? slot = null, Hash256? root = null, Action<DataColumnSidecarGloas>? mutate = null)
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, slot ?? BlockSlot, root ?? BlockRoot);
        sidecar.Index = index;
        mutate?.Invoke(sidecar);
        return sidecar;
    }

    private static void Widen(DataColumnSidecarGloas sidecar, int cells)
    {
        sidecar.Column = [.. Enumerable.Repeat(sidecar.Column![0], cells)];
        sidecar.KzgProofs = [.. Enumerable.Repeat(sidecar.KzgProofs![0], cells)];
    }

    private static BeaconChainSpec WithBlobEntry(BeaconChainSpec spec, BlobScheduleEntry entry) => new()
    {
        SecondsPerSlot = spec.SecondsPerSlot,
        SlotsPerEpoch = spec.SlotsPerEpoch,
        GenesisTime = spec.GenesisTime,
        GenesisValidatorsRoot = spec.GenesisValidatorsRoot,
        Forks = spec.Forks,
        BlobSchedule = [.. spec.BlobSchedule, entry],
        ElectraForkEpoch = spec.ElectraForkEpoch,
        FuluForkEpoch = spec.FuluForkEpoch,
        MaxBlobsPerBlockElectra = spec.MaxBlobsPerBlockElectra,
        GloasForkEpoch = spec.GloasForkEpoch,
        GloasForkVersion = spec.GloasForkVersion,
        Bootnodes = spec.Bootnodes,
    };

    private static byte[] Encode(DataColumnSidecarGloas sidecar) => Snappy.CompressToArray(DataColumnSidecarGloas.Encode(sidecar));

    private static SszKzgCommitment DistinctProof(int variant)
    {
        byte[] proof = new byte[SszKzgCommitment.KzgCommitmentLength];
        BitConverter.TryWriteBytes(proof, variant);
        return SszKzgCommitment.FromSpan(proof);
    }

    private static TestCaseData Case(string name, DataColumnSidecarGloas sidecar, ulong subnet, MessageValidity expected, ColumnGossipDropReason? reason, Outcome outcome) =>
        new TestCaseData(sidecar, subnet, expected, reason, outcome).SetName(name);

    private sealed class ForwardingTopic : ITopic
    {
        public event Action<byte[]>? OnMessage;

        public bool IsSubscribed => true;

        public void Subscribe() { }

        public void Unsubscribe() { }

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }

        public void Deliver(byte[] message) => OnMessage?.Invoke(message);
    }
}
