// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class CommitmentEmitterTests
{
    private static readonly CommitmentDepthPolicy Policy = new(intervalLog2: CommitmentDepthPolicy.MinIntervalLog2);
    private static readonly TreePath CheckpointedPath = TreePath.FromHexString("abc");
    private static readonly TreePath StorageTop = TreePath.FromHexString("7");
    private static readonly ValueHash256 StorageAccount = TestItem.KeccakB.ValueHash256;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private CommitmentMetadata _metadata = null!;

    [SetUp]
    public void SetUp()
    {
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _metadata = new CommitmentMetadata(_historyColumns);
    }

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    [Test]
    public void A_full_vector_window_stays_full_when_an_older_block_is_merged_under_a_newer_row()
    {
        ulong fullWindow = CommitmentDepthPolicy.FullVectorEvery;
        ulong closing = fullWindow * Policy.Interval;
        byte[]?[] children = Children(0, 1, 2);

        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata.WindowWriteLock))
        {
            walk.BeginBlock(closing);
            walk.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(children));
            walk.CompleteBlock();
            walk.FlushOpenWindows();
        }

        children[5] = TestItem.KeccakF.BytesToArray();
        using (CommitmentEmitter tip = CommitmentEmitter.ForTip(_historyColumns, Policy, _metadata.WindowWriteLock))
        {
            tip.BeginBlock(closing - 1);
            tip.RecordAccountNode(CheckpointedPath, BranchRlp.Encode(children));
            tip.CompleteBlock();
        }

        byte[] row = WindowRow(CheckpointedPath, fullWindow)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParentRowCodec.LastBlock(row), Is.EqualTo(closing), "the newer block keeps the row");
            Assert.That(ParentRowCodec.Changed(row) & ParentRowCodec.Presence(row), Is.EqualTo(ParentRowCodec.Presence(row)),
                "a full-vector window must carry every present child however the two emitters interleave, or the backward walk that relies on it never terminates early");
        }
    }

    [Test]
    public void A_child_that_appears_and_vanishes_inside_a_window_keeps_its_changed_bit()
    {
        using CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata.WindowWriteLock);
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
    public void A_storage_trie_that_once_reached_the_signal_depth_stays_large_for_a_later_emitter()
    {
        using (CommitmentEmitter first = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata.WindowWriteLock))
        {
            first.BeginBlock(1);
            first.RecordStorageNode(StorageAccount, TreePath.FromHexString("7abcde"), LeafRlp());
            first.RecordStorageNode(StorageAccount, StorageTop, BranchRlp.Encode(Children(0xa, 0xb)));
            first.CompleteBlock();
            first.FlushOpenWindows();
        }

        using (CommitmentEmitter second = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata.WindowWriteLock))
        {
            second.BeginBlock(2);
            second.RecordStorageNode(StorageAccount, StorageTop, BranchRlp.Encode(Children(0xa, 0xc)));
            second.CompleteBlock();
            second.FlushOpenWindows();
        }

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments));
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WriteScopedPathPrefix(prefix, StorageAccount.Bytes[..CommitmentKeyLayout.IdentityLength], StorageTop, exact: true);
        using CommitmentStore.RowChain exact = store.OpenAtOrBelow(prefix[..prefixLength], 2);

        Assert.That(exact.MoveNext() && exact.CurrentSuffix == 2, Is.True,
            "a block that only touches the top of a large trie must still write an exact row there; a per-block verdict would split the node's history across two chains");
    }

    [Test]
    public void A_commitment_row_scan_is_charged_against_the_proof_budget()
    {
        using (CommitmentEmitter walk = CommitmentEmitter.ForWalk(_historyColumns, Policy, _metadata.WindowWriteLock))
        {
            for (ulong block = 1; block <= 3; block++)
            {
                walk.BeginBlock(block);
                walk.RecordAccountNode(TreePath.FromHexString("a"), BranchRlp.Encode(Children((int)block)));
                walk.CompleteBlock();
            }
        }

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments));
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, TreePath.FromHexString("a"), exact: true);
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_metadata.TryPublishVerifiedCoverage(0, 50, out _, out _), Is.True);
            Assert.That(_metadata.TryPublishVerifiedCoverage(100, 200, out ulong keptFrom, out ulong keptTo), Is.False,
                "coverage is one contiguous range; a disjoint build cannot silently replace what was already servable");
            Assert.That((keptFrom, keptTo), Is.EqualTo((0UL, 50UL)));
            Assert.That(_metadata.TryPublishVerifiedCoverage(40, 80, out ulong from, out ulong to), Is.True);
            Assert.That((from, to), Is.EqualTo((0UL, 80UL)), "an overlapping build widens the range");
            Assert.That(_metadata.TryGetCoverage(out ulong storedFrom, out ulong storedTo) && storedFrom == 0 && storedTo == 80, Is.True);
        }
    }

    [Test]
    public void A_depth_that_does_not_fit_the_stamp_is_refused() =>
        Assert.That(() => new CommitmentDepthPolicy(CommitmentDepthPolicy.DefaultIntervalLog2, 1, 16, 2, 4, 6), Throws.InstanceOf<InvalidConfigurationException>(),
            "the layout stamp packs depths into nibbles; a depth above 15 would collide with another layout and defeat the mixing guard");

    private byte[]? WindowRow(in TreePath path, ulong window)
    {
        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments));
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, path, exact: false);
        return store.TryGetExact(prefix[..prefixLength], window);
    }

    private static byte[]?[] Children(params int[] present)
    {
        byte[]?[] children = new byte[]?[BranchRlp.ChildCount];
        foreach (int index in present)
        {
            byte[] hash = new byte[Hash256.Size];
            hash[0] = (byte)(index + 1);
            children[index] = hash;
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
