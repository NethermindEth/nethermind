// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class CommitmentEmitterTests
{
    private static readonly CommitmentDepthPolicy Policy = new(CommitmentDepthPolicy.MinIntervalLog2, CommitmentDepthPolicy.DefaultAccountExactDepth, CommitmentDepthPolicy.DefaultAccountCheckpointDepth, CommitmentDepthPolicy.DefaultStorageExactDepth, CommitmentDepthPolicy.DefaultStorageCheckpointDepth, CommitmentDepthPolicy.DefaultLargeTrieSignalDepth, storageRowsSignalDepth: 1);
    private static readonly TreePath CheckpointedPath = TreePath.FromHexString("abc");
    private static readonly TreePath StorageTop = TreePath.FromHexString("7");
    private static readonly ValueHash256 StorageAccount = TestItem.KeccakB.ValueHash256;
    private const int PinnedNodeSpan = 120_000;
    private const string PinnedRowsDigest = "0x6a57acc267ec3da93a0123198e383190e4ae81471f82bd0f85ed82375e6f5b72";
    private static readonly ulong[] PinnedBlocks = [1, 2, 30, 64, 65, 100, 130, 192, 193, 16_383, 16_384, 16_385];

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private CommitmentMetadata _metadata = null!;

    [SetUp]
    public void SetUp()
    {
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _metadata = new CommitmentMetadata(_historyColumns, Policy);
    }

    [TearDown]
    public void TearDown()
    {
        _metadata.Dispose();
        _historyColumns.Dispose();
    }

    [TestCase(true, TestName = "ExistingRowNewer")]
    [TestCase(false, TestName = "MergedStateNewer")]
    public void A_full_vector_window_carries_every_present_child_whichever_writer_came_first(bool walkFirst)
    {
        ulong fullWindow = CommitmentDepthPolicy.FullVectorEvery;
        ulong closing = fullWindow * Policy.Interval;
        ChildVector newer = Children(0, 1, 2, 5);
        ChildVector older = Children(0, 1, 2);
        older.SetHash(1, TestItem.KeccakF.ValueHash256);

        if (walkFirst)
        {
            WriteWindow(CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata), closing, newer);
            WriteWindow(CommitmentEmitter.ForTip(_historyColumns, Policy, _metadata), closing - 1, older);
        }
        else
        {
            WriteWindow(CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata), closing - 1, older);
            WriteWindow(CommitmentEmitter.ForTip(_historyColumns, Policy, _metadata), closing, newer);
        }

        byte[] row = WindowRow(CheckpointedPath, fullWindow)!;
        ChildVector carried = ChildVector.Rent();
        ushort presence = ParentRowCodec.Presence(row);
        ushort filled = ParentRowCodec.Fill(row, presence, carried);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParentRowCodec.LastBlock(row), Is.EqualTo(closing), "the newer block keeps the row");
            Assert.That(filled, Is.EqualTo(presence), "a full-vector window carries a reference for every present child however the two writers interleave");
            Assert.That(carried[1], Is.SequenceEqualTo(newer[1]), "a child both writers carried resolves to the newer block's reference");
            Assert.That(carried[5], Is.SequenceEqualTo(newer[5]), "a child only the newer block carried is present in the merged row");
        }

        ChildVector.Return(carried);
        ChildVector.Return(newer);
        ChildVector.Return(older);
    }

    [Test]
    public void A_child_that_vanished_before_the_window_closed_keeps_its_changed_bit_when_the_other_writer_is_newer()
    {
        const ulong window = 3;
        ulong closing = window * Policy.Interval;
        ChildVector tipChildren = Children(0, 1, 2);
        ChildVector walkChildren = Children(0, 1, 2);
        WriteWindow(CommitmentEmitter.ForTip(_historyColumns, Policy, _metadata), closing, tipChildren);
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            walk.BeginBlock(closing - 1);
            walk.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(walkChildren), changedChildren: (ushort)(0b111 | (1 << 5)));
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        byte[] row = WindowRow(CheckpointedPath, window)!;
        ChildVector merged = ChildVector.Rent();
        ParentRowCodec.Fill(row, ParentRowCodec.Changed(row), merged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParentRowCodec.LastBlock(row), Is.EqualTo(closing), "the tip's later block keeps the row");
            Assert.That((ParentRowCodec.Changed(row) >> 5) & 1, Is.EqualTo(1),
                "child 5 appeared and vanished inside the window before the newer writer started observing; its changed bit must survive the merge or a proof at a block where it lived takes the anchor's reference and fails its parent's check");
            Assert.That(merged.IsPresent(5), Is.False, "a vanished child carries no reference");
        }

        ChildVector.Return(merged);
        ChildVector.Return(tipChildren);
        ChildVector.Return(walkChildren);
    }

    [Test]
    public void The_tip_emitter_skips_per_block_rows_for_an_epoch_below_the_fine_floor()
    {
        _metadata.TryRaiseFineFromEpoch(1);
        using (CommitmentEmitter tip = CommitmentEmitter.ForTip(_historyColumns, Policy, _metadata))
        {
            tip.BeginBlock(1);
            tip.RecordAccountNode(TreePath.Empty, BranchRlp.Encode(Children(5, 9)));
            tip.CompleteBlock();
            tip.FlushOpenWindows();
        }

        IDb accounts = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.GetAllKeys().Any(key => key.Length > CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength && key[CommitmentKeyLayout.EpochLength] == CommitmentKeyLayout.FineTier), Is.False,
                "the reclaimer has already removed this epoch's per-block rows; writing new ones there leaves rows no pass will ever revisit");
            Assert.That(WindowRow(TreePath.Empty, Policy.WindowClosingAt(1)), Is.Not.Null, "the window row is still written, that tier was not demoted");
        }
    }

    [Test]
    public void A_node_one_level_below_the_deepest_checkpoint_marks_its_parents_changed_child()
    {
        TreePath deepest = TreePath.FromHexString("abcde");
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            walk.BeginBlock(1);
            walk.RecordAccountNode(deepest, BranchRlp.Encode(Children(5, 9)));
            walk.CompleteBlock();
            walk.BeginBlock(2);
            walk.RecordAccountNode(deepest, BranchRlp.Encode(Children(5, 9)));
            walk.RecordAccountNode(deepest.Append(5), LeafRlp());
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        byte[] row = WindowRow(deepest, Policy.WindowClosingAt(2))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((ParentRowCodec.Changed(row) >> 5) & 1, Is.EqualTo(1),
                "the child at the first uncommitted depth is only known to have changed through its own commit record, so its key must be kept even though its bytes are dropped");
            Assert.That((ParentRowCodec.Changed(row) >> 9) & 1, Is.Zero, "a child nobody touched stays unchanged");
        }
    }

    [Test]
    public void A_subtree_that_empties_inside_a_window_leaves_an_empty_row_not_its_last_branch()
    {
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            walk.BeginBlock(1);
            walk.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(Children(0, 1)));
            walk.CompleteBlock();
            walk.BeginBlock(2);
            walk.RecordAccountEmpty(CheckpointedPath);
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        Assert.That(ParentRowCodec.IsEmptyRow(WindowRow(CheckpointedPath, Policy.WindowClosingAt(2))!), Is.True,
            "a subtree that vanished must not leave its previous branch readable at that window, or a fold above it would keep a child that no longer exists");
    }

    [Test]
    public void A_branch_that_carries_a_value_is_refused_by_the_child_reader()
    {
        byte[] valued = new byte[19];
        valued[0] = 0xD2;
        for (int i = 1; i <= 16; i++) valued[i] = 0x80;
        valued[17] = 0x81;
        valued[18] = 0x7F;
        ChildVector children = ChildVector.Rent();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => BranchRlp.ReadChildren(valued, children), Throws.InstanceOf<InvalidDataException>(),
                "state and storage tries key by fixed-width hashes, so no branch can carry a value; one that does is not a node of these tries and must not round-trip to a different node");
            Assert.That(BranchRlp.TryReadChildren(valued, children), Is.False, "the Try form reports the same node as not a branch, so the emitter falls through to a whole-node row instead of aborting the block");
        }

        ChildVector.Return(children);
    }

    private static void WriteWindow(CommitmentEmitter emitter, ulong block, ChildVector children)
    {
        using (emitter)
        {
            emitter.BeginBlock(block);
            emitter.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(children));
            emitter.CompleteBlock();
            emitter.FlushOpenWindows();
        }
    }

    [Test]
    public void A_child_that_appears_and_vanishes_inside_a_window_keeps_its_changed_bit()
    {
        using CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata);
        walk.BeginBlock(1);
        walk.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(Children(0, 1)));
        walk.CompleteBlock();
        walk.BeginBlock(2);
        walk.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(Children(0, 1, 9)));
        walk.RecordAccountNode(CheckpointedPath.Append(9), LeafRlp());
        walk.CompleteBlock();
        walk.BeginBlock(3);
        walk.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(Children(0, 1)));
        walk.CompleteBlock();
        walk.FlushOpenWindows();

        byte[] row = WindowRow(CheckpointedPath, Policy.WindowClosingAt(3))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((ParentRowCodec.Presence(row) >> 9) & 1, Is.Zero, "the child is absent at the window's last block");
            Assert.That((ParentRowCodec.Changed(row) >> 9) & 1, Is.EqualTo(1),
                "the resolver re-resolves changed children at the queried block; without the bit a query between creation and deletion would rebuild the whole subtree");
        }
    }

    [Test]
    public void A_branch_written_over_a_whole_row_of_the_same_window_marks_every_present_child_changed()
    {
        using CommitmentEmitter tip = CommitmentEmitter.ForTip(_historyColumns, Policy, _metadata);
        tip.BeginBlock(1);
        tip.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(Children(0, 1)));
        tip.CompleteBlock();
        tip.FlushOpenWindows();
        tip.BeginBlock(2);
        tip.RecordAccountNode(CheckpointedPath, LeafRlp());
        tip.CompleteBlock();
        tip.FlushOpenWindows();
        tip.BeginBlock(3);
        tip.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(Children(0, 1)));
        tip.RecordAccountNode(CheckpointedPath.Append(1), LeafRlp());
        tip.CompleteBlock();
        tip.FlushOpenWindows();

        byte[] row = WindowRow(CheckpointedPath, Policy.WindowClosingAt(3))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParentRowCodec.Presence(row) & 0b11, Is.EqualTo(0b11));
            Assert.That(ParentRowCodec.Changed(row) & 0b11, Is.EqualTo(0b11),
                "the row on disk was a whole node when this branch was flushed over it, so every child the branch presents moved relative to the window anchor; a mask that names only child 1 would send child 0's readers to a checkpoint chain that predates the leaf");
        }
    }

    [Test]
    public void A_storage_trie_that_once_reached_the_signal_depth_stays_large_for_a_later_emitter()
    {
        using (CommitmentEmitter first = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            first.BeginBlock(1);
            first.RecordStorageNode(StorageAccount, TreePath.FromHexString("7abcde"), LeafRlp());
            first.RecordStorageNode(StorageAccount, StorageTop, BranchRlp.Encode(Children(0xa, 0xb)));
            first.CompleteBlock();
            first.FlushOpenWindows();
        }

        using (CommitmentEmitter second = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            second.BeginBlock(2);
            second.RecordStorageNode(StorageAccount, StorageTop, BranchRlp.Encode(Children(0xa, 0xc)));
            second.CompleteBlock();
            second.FlushOpenWindows();
        }

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments), Policy, CommitmentKeyLayout.IdentityLength);
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WriteScopedPathPrefix(prefix, StorageAccount.Bytes[..CommitmentKeyLayout.IdentityLength], StorageTop, exact: true);
        using CommitmentStore.RowChain exact = store.OpenAtOrBelow(prefix[..prefixLength], 2);

        Assert.That(exact.MoveNext() && exact.CurrentSuffix == 2, Is.True,
            "a block that only touches the top of a large trie must still write an exact row there; a per-block verdict would split the node's history across two chains");
    }

    [Test]
    public void A_commitment_row_scan_is_charged_against_the_proof_budget()
    {
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            for (ulong block = 1; block <= 3; block++)
            {
                walk.BeginBlock(block);
                walk.RecordAccountNode(TreePath.FromHexString("ab"), BranchRlp.Encode(Children((int)block)));
                walk.CompleteBlock();
            }
        }

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments), Policy, 0);
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, TreePath.FromHexString("ab"), exact: true);
        using CommitmentStore.RowChain chain = store.OpenAtOrBelow(prefix[..prefixLength], 3, new ResolutionBudget(2));

        Assert.That(() => { while (chain.MoveNext()) { } }, Throws.InstanceOf<StateUnavailableException>(),
            "every row a proof touches, commitment or history, comes out of one budget, or a corrupt chain turns one request into a full-column scan");
    }

    [Test]
    public void The_node_cache_is_keyed_by_the_hash_the_parent_commits_to()
    {
        ArchiveProofNodeCache cache = new(16);
        byte[] rlp = LeafRlp();
        cache.Set(Keccak.Compute(rlp), rlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(Keccak.Compute(rlp), out byte[]? hit) && hit!.SequenceEqual(rlp), Is.True);
            Assert.That(cache.TryGet(TestItem.KeccakA.ValueHash256, out _), Is.False,
                "a cached node is only ever returned for the exact hash it verified against, so a reorg at a covered height cannot hand back the old chain's node");
        }
    }

    [Test]
    public void Coverage_grows_by_union_and_refuses_a_disjoint_range()
    {
        Assert.That(_metadata.TryPublishVerifiedCoverage(0, 50, out _, out _), Is.True);
        Assert.That(_metadata.TryPublishVerifiedCoverage(100, 200, out ulong keptFrom, out ulong keptTo), Is.False,
            "coverage is one contiguous range; a disjoint build cannot silently replace what was already servable");
        Assert.That((keptFrom, keptTo), Is.EqualTo((0UL, 50UL)));
        Assert.That(_metadata.TryPublishVerifiedCoverage(40, 80, out ulong from, out ulong to), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((from, to), Is.EqualTo((0UL, 80UL)), "an overlapping build widens the range");
            Assert.That(_metadata.TryGetCoverage(out ulong storedFrom, out ulong storedTo) && storedFrom == 0 && storedTo == 80, Is.True);
        }
    }

    [TestCase(1, 16, 2, 4, 6, 4, TestName = "AccountCheckpointDepth")]
    [TestCase(1, 7, 2, 16, 20, 4, TestName = "StorageCheckpointDepth")]
    [TestCase(1, 5, 2, 4, 6, 7, TestName = "StorageRowsSignalAboveTheLargeTrieSignal")]
    [TestCase(1, 5, 2, 4, 6, 0, TestName = "StorageRowsSignalZero")]
    public void A_depth_that_does_not_fit_the_stamp_is_refused(int accountExact, int accountCheckpoint, int storageExact, int storageCheckpoint, int signal, int rowsSignal) =>
        Assert.That(() => new CommitmentDepthPolicy(CommitmentDepthPolicy.DefaultIntervalLog2, accountExact, accountCheckpoint, storageExact, storageCheckpoint, signal, rowsSignal, accountExact == 1 ? 0 : CommitmentDepthPolicy.DefaultAccountComposedDepths), Throws.InstanceOf<InvalidConfigurationException>(),
            "the layout stamp packs depths into nibbles; a depth above 15 would collide with another layout and defeat the mixing guard");

    [TestCase(1, TestName = "TheRoot")]
    [TestCase(1 << 2, TestName = "TheExactDepthItself")]
    public void A_composed_depth_outside_the_exact_top_is_refused(int composed) =>
        Assert.That(() => new CommitmentDepthPolicy(CommitmentDepthPolicy.DefaultIntervalLog2, 2, 5, 2, 4, 6, 4, composed), Throws.InstanceOf<InvalidConfigurationException>(),
            "a composed depth is rebuilt from its children's exact rows, so it must sit strictly between the stored root and the exact depth");

    [Test]
    public void A_composed_account_depth_writes_no_row_and_its_children_keep_theirs()
    {
        TreePath root = TreePath.Empty;
        TreePath composed = TreePath.FromHexString("a");
        TreePath child = TreePath.FromHexString("ab");
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            walk.BeginBlock(1);
            walk.RecordAccountNode(root, BranchRlp.Encode(Children(0xa)));
            walk.RecordAccountNode(composed, BranchRlp.Encode(Children(0xb)));
            walk.RecordAccountNode(child, BranchRlp.Encode(Children(1, 2)));
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments), Policy, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExactRow(store, root, 1), Is.Not.Null, "the root keeps its exact row: one seek instead of composing 256 grandchildren");
            Assert.That(ExactRow(store, composed, 1), Is.Null, "a depth-1 row repeats the sixteen depth-2 references that already have exact rows, so it is composed at read time instead of stored");
            Assert.That(ExactRow(store, child, 1), Is.Not.Null, "the depth-2 children the composition reads from keep their exact rows");
        }
    }

    [Test]
    public void An_account_node_below_the_checkpoint_depth_writes_no_row()
    {
        TreePath checkpointed = TreePath.FromHexString("abcde");
        TreePath below = checkpointed.Append(0xf);
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            walk.BeginBlock(1);
            walk.RecordAccountNode(checkpointed, BranchRlp.Encode(Children(0xf)));
            walk.RecordAccountNode(below, BranchRlp.Encode(Children(1, 2)));
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WindowRow(checkpointed, Policy.WindowClosingAt(1)), Is.Not.Null, "the deepest checkpointed depth keeps its window row");
            Assert.That(WindowRow(below, Policy.WindowClosingAt(1)), Is.Null,
                "a node one level below covers a few accounts; rebuilding it from their rows is one range scan, cheaper than the rows it would otherwise cost at every window");
        }
    }

    [Test]
    public void A_storage_trie_writes_rows_only_once_it_has_reached_the_rows_signal_depth()
    {
        CommitmentDepthPolicy policy = new(intervalLog2: CommitmentDepthPolicy.MinIntervalLog2);
        IDb column = _historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments);
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, policy, _metadata))
        {
            walk.BeginBlock(1);
            walk.RecordStorageNode(StorageAccount, StorageTop, BranchRlp.Encode(Children(0xa, 0xb)));
            walk.RecordStorageNode(StorageAccount, TreePath.FromHexString("7a"), LeafRlp());
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        Assert.That(column.GetAllKeys(), Is.Empty, "a small trie rebuilds whole from its slot rows in one scan; rows for it would only duplicate the history");

        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, policy, _metadata))
        {
            walk.BeginBlock(2);
            walk.RecordStorageNode(StorageAccount, StorageTop, BranchRlp.Encode(Children(0xa, 0xc)));
            walk.RecordStorageNode(StorageAccount, TreePath.FromHexString("7abc"), LeafRlp());
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        CommitmentStore store = new(column, policy, CommitmentKeyLayout.IdentityLength);
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WriteScopedPathPrefix(prefix, StorageAccount.Bytes[..CommitmentKeyLayout.IdentityLength], StorageTop, exact: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetExact(prefix[..prefixLength], policy.WindowClosingAt(2)), Is.Not.Null, "once the trie is deep enough that a whole rebuild is no longer one cheap scan, its top gets checkpoint rows");
            Assert.That(store.ReadStorageTrieDepth(StorageAccount), Is.EqualTo(CommitmentDepthPolicy.DefaultStorageRowsSignalDepth), "the depth reached is persisted so the read side and later emitters agree");
        }
    }

    [Test]
    public void FlushWindows_AcrossTheOpenNodeCapAndSeveralWindows_WritesPinnedRows()
    {
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata))
        {
            foreach (ulong block in PinnedBlocks)
            {
                walk.BeginBlock(block);
                int count = block == PinnedBlocks[0] ? PinnedNodeSpan : PinnedNodeSpan / 16;
                for (int node = 0; node < count; node++) RecordPinnedNode(walk, block, (int)((node * 7L + (long)block) % PinnedNodeSpan));
                walk.CompleteBlock();
            }

            walk.FlushOpenWindows();
        }

        Assert.That(CommitmentRowsDigest(), Is.EqualTo(PinnedRowsDigest),
            "the digest covers every account and storage commitment row this sequence writes; a change to how open nodes are held must leave it byte-identical");
    }

    [Test]
    public void Accumulate_AfterTheFirstWindowFlushed_DoesNotAllocatePerOpenNode()
    {
        const int nodes = 10_000;
        TreePath[] paths = new TreePath[nodes];
        byte[][] rlps = new byte[nodes][];
        for (int node = 0; node < nodes; node++)
        {
            paths[node] = PinnedPath(node * 97, CommitmentDepthPolicy.DefaultAccountCheckpointDepth);
            ChildVector children = Children(node % 16, (node + 5) % 16);
            rlps[node] = BranchRlp.Encode(children);
            ChildVector.Return(children);
        }

        using CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata);
        RecordAll(walk, 1, paths, rlps);
        RecordAll(walk, Policy.Interval, paths, rlps);
        walk.BeginBlock(Policy.Interval + 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int node = 0; node < nodes; node++) walk.RecordAccountNode(paths[node], rlps[node]);
        walk.CompleteBlock();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.LessThan(64 * 1024),
            "once a window has been flushed the open-node storage is reused, so the next window's nodes cost no new heap objects");
    }

    private static byte[]? ExactRow(CommitmentStore store, in TreePath path, ulong block)
    {
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, path, exact: true);
        return store.TryGetExact(prefix[..prefixLength], block);
    }

    private byte[]? WindowRow(in TreePath path, ulong window)
    {
        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments), Policy, 0);
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, path, exact: false);
        return store.TryGetExact(prefix[..prefixLength], window);
    }

    private static void RecordAll(CommitmentEmitter emitter, ulong block, TreePath[] paths, byte[][] rlps)
    {
        emitter.BeginBlock(block);
        for (int node = 0; node < paths.Length; node++) emitter.RecordAccountNode(paths[node], rlps[node]);
        emitter.CompleteBlock();
    }

    private static void RecordPinnedNode(CommitmentEmitter emitter, ulong block, int node)
    {
        int depth = 3 + node % 3;
        TreePath path = PinnedPath(node, depth);
        byte[] seed = Keccak.Compute(BitConverter.GetBytes(node * 1_000_003L + (long)block)).BytesToArray();
        switch ((node + (int)block) % 7)
        {
            case 0:
                emitter.RecordAccountEmpty(path);
                break;
            case 1:
                emitter.RecordAccountNode(path, node % 97 == 0 ? LongLeafRlp(seed) : LeafRlp(seed));
                break;
            case 2:
                emitter.RecordAccountNode(path, PinnedBranch(node, seed), (ushort)(seed[2] | (seed[3] << 8)));
                break;
            default:
                emitter.RecordAccountNode(path, PinnedBranch(node, seed));
                if (depth == CommitmentDepthPolicy.DefaultAccountCheckpointDepth) emitter.RecordAccountNode(path.Append(seed[4] & 0xf), LeafRlp(seed));
                break;
        }

        if (node % 50 == 0)
        {
            emitter.RecordStorageNode(StorageAccount, PinnedPath(node, 1 + node % 4), PinnedBranch(node, seed));
        }
    }

    private static TreePath PinnedPath(int node, int depth)
    {
        Span<byte> nibbles = stackalloc byte[depth];
        for (int index = 0; index < depth; index++) nibbles[index] = (byte)((node >> (4 * index)) & 0xf);
        return TreePath.FromNibble(nibbles);
    }

    private static byte[] PinnedBranch(int node, byte[] seed)
    {
        ChildVector children = ChildVector.Rent();
        ushort presence = (ushort)(seed[0] | (seed[1] << 8) | 0b11);
        for (int index = 0; index < 16; index++)
        {
            if (((presence >> index) & 1) == 0) continue;

            byte[] hash = new byte[Hash256.Size];
            hash[0] = (byte)(index + 1);
            BitConverter.TryWriteBytes(hash.AsSpan(1), node);
            if (index % 3 == 0) hash[31] = seed[5 + index];
            children.Set(index, hash);
        }

        byte[] rlp = BranchRlp.Encode(children);
        ChildVector.Return(children);
        return rlp;
    }

    private static byte[] LeafRlp(byte[] seed)
    {
        byte[] rlp = LeafRlp();
        seed.CopyTo(rlp.AsSpan(3));
        return rlp;
    }

    private static byte[] LongLeafRlp(byte[] seed)
    {
        const int payload = 600;
        byte[] rlp = new byte[6 + payload];
        rlp[0] = 0xF9;
        rlp[1] = (payload + 3) >> 8;
        rlp[2] = (payload + 3) & 0xff;
        rlp[3] = 0xB9;
        rlp[4] = payload >> 8;
        rlp[5] = payload & 0xff;
        for (int index = 6; index < rlp.Length; index += seed.Length) seed.AsSpan(0, Math.Min(seed.Length, rlp.Length - index)).CopyTo(rlp.AsSpan(index));
        return rlp;
    }

    private string CommitmentRowsDigest()
    {
        using MemoryStream rows = new();
        foreach (FlatHistoryColumns column in new[] { FlatHistoryColumns.AccountCommitments, FlatHistoryColumns.StorageCommitments })
        {
            foreach (KeyValuePair<byte[], byte[]> row in _historyColumns.GetColumnDb(column).GetAll().OrderBy(static row => row.Key, Bytes.Comparer))
            {
                rows.Write(BitConverter.GetBytes(row.Key.Length));
                rows.Write(row.Key);
                rows.Write(BitConverter.GetBytes(row.Value.Length));
                rows.Write(row.Value);
            }
        }

        return Keccak.Compute(rows.ToArray()).ToString();
    }

    private static ChildVector Children(params int[] present)
    {
        ChildVector children = ChildVector.Rent();
        foreach (int index in present)
        {
            byte[] hash = new byte[Hash256.Size];
            hash[0] = (byte)(index + 1);
            children.Set(index, hash);
        }

        return children;
    }

    private static byte[] LeafRlp()
    {
        byte[] rlp = new byte[64];
        rlp[0] = 0xF8;
        rlp[1] = 62;
        rlp[2] = 0xA0;
        return rlp;
    }
}
