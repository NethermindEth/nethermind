// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.Test;
using Nethermind.Core.Buffers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ForwardCommitmentCaptureTests
{
    private static readonly TreePath PerChangePath = TreePath.FromHexString("a");
    private static readonly TreePath CheckpointedPath = TreePath.FromHexString("abc");
    private static readonly CommitmentDepthPolicy Policy = new(CommitmentDepthPolicy.DefaultIntervalLog2, CommitmentDepthPolicy.DefaultAccountExactDepth, CommitmentDepthPolicy.DefaultAccountCheckpointDepth, CommitmentDepthPolicy.DefaultStorageExactDepth, CommitmentDepthPolicy.DefaultStorageCheckpointDepth, CommitmentDepthPolicy.DefaultLargeTrieSignalDepth, storageRowsSignalDepth: 1);
    private static readonly TreePath StoragePath = TreePath.FromHexString("7f");
    private static readonly ValueHash256 StorageAccount = TestItem.KeccakB.ValueHash256;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private ResourcePool _resourcePool = null!;
    private FlatTestContainer _tier = null!;
    private SnapshotRepository _repository = null!;
    private HistoryWriter _writer = null!;
    private CommitmentMetadata _metadata = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _resourcePool = new ResourcePool(new FlatDbConfig { CompactSize = 16 });
        _tier = new FlatTestContainer(new FlatDbConfig { CompactSize = 16 });
        _repository = _tier.Repository;

        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _metadata = new CommitmentMetadata(_historyColumns);
        ForwardCommitmentCapture capture = new(_historyColumns, Policy, _metadata, new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance);
        _writer = new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance, capture);
    }

    [TearDown]
    public void TearDown()
    {
        _tier.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void A_per_change_node_gets_one_row_per_block_even_though_the_walk_visits_blocks_head_first()
    {
        CommitBlock(0, PerChangePath, LeafRlp(0));
        CommitBlock(1, PerChangePath, LeafRlp(1));
        CommitBlock(2, PerChangePath, LeafRlp(2));
        CommitBlock(3, PerChangePath, LeafRlp(3));

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 0; block <= 3; block++)
            {
                Assert.That(AccountRowAtOrBelow(PerChangePath, block, exact: true), Is.EqualTo(LeafRlp((int)block)),
                    $"the row at block {block} must hold that block's node, whichever order the capture walked");
            }
        }
    }

    [Test]
    public void A_checkpointed_node_row_holds_the_newest_value_of_its_window()
    {
        CommitBlock(0, CheckpointedPath, LeafRlp(0));
        CommitBlock(1, CheckpointedPath, LeafRlp(1));
        CommitBlock(2, CheckpointedPath, LeafRlp(2));

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        ulong window = 1;
        Assert.That(AccountRowAtOrBelow(CheckpointedPath, window, exact: false), Is.EqualTo(LeafRlp(2)),
            "a head-first capture must still leave the window with the value of its last block, not its first");
    }

    [Test]
    public void A_node_the_block_removed_leaves_a_tombstone()
    {
        CommitBlock(0, PerChangePath, LeafRlp(0));
        CommitBlock(1, PerChangePath, []);

        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Assert.That(AccountRowAtOrBelow(PerChangePath, 1, exact: true), Is.EqualTo(LeafRlp(0)),
            "a node the block removed writes nothing; its parent's row no longer lists it, and the last row it has is the older one");
    }

    [Test]
    public void A_capture_round_holding_more_trie_bytes_than_the_bound_stops_instead_of_growing()
    {
        HistoryWriter writer = CreateWriter(maxBufferedBytes: 100);
        for (ulong block = 0; block <= 3; block++) CommitBlock(block, PerChangePath, LeafRlp((int)block));

        writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountRowAtOrBelow(PerChangePath, 3, exact: true), Is.Null,
                "a round whose buffered trie nodes exceed the byte bound is dropped whole; the retrofit walk owns that range");
            Assert.That(_metadata.TryGetTipSeries(out _, out _), Is.False, "nothing was replayed, so no tip series may claim the range");
        }
    }

    [Test]
    public void A_capture_round_that_reaches_the_large_trie_depth_writes_exact_rows_at_the_tries_top()
    {
        TreePath deep = TreePath.FromHexString("7abcde");
        Snapshot genesis = _resourcePool.CreateSnapshot(StateId.PreGenesis, StateAt(0), ResourcePool.Usage.ReadOnlyProcessingEnv);
        genesis.Content.StorageNodes[(StorageAccount.ToCommitment(), TreePath.FromHexString("7"))] = new TrieNode(NodeType.Branch, BranchRlp.Encode(ChildrenAt(0xa, 0xb)));
        genesis.Content.StorageNodes[(StorageAccount.ToCommitment(), deep)] = new TrieNode(NodeType.Leaf, LeafRlp(3));
        Add(genesis, 0);

        _writer.CaptureUpTo(StateAt(0), _repository, CancellationToken.None);

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments));
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WriteScopedPathPrefix(prefix, StorageAccount.Bytes[..CommitmentKeyLayout.IdentityLength], TreePath.FromHexString("7"), exact: true);
        using CommitmentStore.RowChain exact = store.OpenAtOrBelow(prefix[..prefixLength], 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exact.MoveNext() && exact.CurrentSuffix == 0, Is.True,
                "a node at the signal depth in the captured round marks the trie large, so its top gets an exact row from the capture path as well as from the walk");
            Assert.That(store.ReadStorageTrieDepth(StorageAccount), Is.EqualTo(CommitmentDepthPolicy.DefaultLargeTrieSignalDepth), "the decision is persisted so later rounds stay large");
        }
    }

    [Test]
    public void A_storage_node_is_recorded_under_its_accounts_identity()
    {
        Snapshot genesis = _resourcePool.CreateSnapshot(StateId.PreGenesis, StateAt(0), ResourcePool.Usage.ReadOnlyProcessingEnv);
        genesis.Content.StorageNodes[(StorageAccount.ToCommitment(), StoragePath)] = new TrieNode(NodeType.Leaf, LeafRlp(9));
        Add(genesis, 0);

        _writer.CaptureUpTo(StateAt(0), _repository, CancellationToken.None);

        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments));
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WriteScopedPathPrefix(prefix, StorageAccount.Bytes[..CommitmentKeyLayout.IdentityLength], StoragePath, exact: false);
        using CommitmentStore.RowChain chain = store.OpenAtOrBelow(prefix[..prefixLength], 1);

        Assert.That(chain.MoveNext() && ParentRowCodec.WholeNodeRlp(chain.CurrentValue).SequenceEqual(LeafRlp(9)), Is.True,
            "storage commitments are keyed by the account's twenty-byte identity, the same width the storage history rows carry");
    }

    [Test]
    public void The_published_coverage_follows_the_tip_once_the_retrofit_walk_has_reached_it()
    {
        _metadata.TryPublishVerifiedCoverage(0, 1, out _, out _);
        for (ulong block = 0; block <= 3; block++) CommitBlock(block, PerChangePath, LeafRlp((int)block));

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_metadata.TryGetCoverage(out ulong from, out ulong to) && from == 0 && to == 3, Is.True,
                "coverage published by the walk extends over the tip series that continues it");
            Assert.That(_metadata.TryGetTipSeries(out ulong start, out ulong frontier) && start == 0 && frontier == 3, Is.True);
        }
    }

    [Test]
    public void A_series_that_began_at_genesis_publishes_its_own_coverage()
    {
        for (ulong block = 0; block <= 2; block++) CommitBlock(block, PerChangePath, LeafRlp((int)block));

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_metadata.TryGetCoverage(out ulong from, out ulong to) && from == 0 && to == 2, Is.True,
                "a tip series that began at genesis is complete on its own, so a from-genesis node needs no retrofit walk");
            Assert.That(_metadata.TryGetTipSeries(out ulong start, out ulong frontier) && start == 0 && frontier == 2, Is.True);
        }
    }

    private HistoryWriter CreateWriter(long maxBufferedBytes)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        ForwardCommitmentCapture bounded = new(
            _historyColumns, Policy, _metadata, new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance, maxBufferedBytes);
        return new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance, bounded);
    }

    private void CommitBlock(ulong block, in TreePath path, byte[] rlp)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(
            block == 0 ? StateId.PreGenesis : StateAt(block - 1), StateAt(block), ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.StateNodes[path] = new TrieNode(rlp.Length == 0 ? NodeType.Unknown : NodeType.Leaf, rlp);
        Add(snapshot, block);
    }

    private void Add(Snapshot snapshot, ulong block)
    {
        Assert.That(_repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True, "precondition: the block's snapshot is in the repository");
        _repository.AddStateId(StateAt(block));
    }

    private byte[]? AccountRowAtOrBelow(in TreePath path, ulong suffix, bool exact)
    {
        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments));
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, path, exact);
        using CommitmentStore.RowChain chain = store.OpenAtOrBelow(prefix[..prefixLength], suffix);
        if (!chain.MoveNext()) return null;
        return ParentRowCodec.WholeNodeRlp(chain.CurrentValue).ToArray();
    }

    private static ChildVector ChildrenAt(params int[] present)
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

    private static byte[] LeafRlp(int tag)
    {
        TrieNode leaf = TrieNodeFactory.CreateLeaf(new byte[60], new CappedArray<byte>(RlpTagged(tag)));
        TreePath path = TreePath.FromHexString("abcd");
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path, canBeParallel: false);
        return leaf.FullRlp.ToArray()!;
    }

    private static byte[] RlpTagged(int tag)
    {
        byte[] rlp = new byte[40];
        rlp[0] = 0xB8;
        rlp[1] = 38;
        rlp[2] = (byte)tag;
        return rlp;
    }

    private static StateId StateAt(ulong blockNumber)
    {
        Span<byte> root = stackalloc byte[32];
        root[0] = (byte)blockNumber;
        return new StateId(blockNumber, new ValueHash256(root));
    }
}
