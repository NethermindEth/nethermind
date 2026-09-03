// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.Test;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ZzScratchEpochTests
{
    private static readonly TreePath Quiet = TreePath.FromHexString("ab");
    private static readonly TreePath Busy = TreePath.FromHexString("cd");

    private static readonly CommitmentDepthPolicy EpochPolicy = new(
        CommitmentDepthPolicy.MinIntervalLog2,
        CommitmentDepthPolicy.DefaultAccountExactDepth,
        CommitmentDepthPolicy.DefaultAccountCheckpointDepth,
        CommitmentDepthPolicy.DefaultStorageExactDepth,
        CommitmentDepthPolicy.DefaultStorageCheckpointDepth,
        CommitmentDepthPolicy.DefaultLargeTrieSignalDepth,
        storageRowsSignalDepth: 1,
        CommitmentDepthPolicy.DefaultAccountComposedDepths,
        epochLog2: CommitmentDepthPolicy.MinIntervalLog2 + 1);

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private ResourcePool _resourcePool = null!;
    private FlatTestContainer _tier = null!;
    private SnapshotRepository _repository = null!;
    private CommitmentMetadata _metadata = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _resourcePool = new ResourcePool(new FlatDbConfig { CompactSize = 16 });
        _tier = new FlatTestContainer(new FlatDbConfig { CompactSize = 16 });
        _repository = _tier.Repository;
        _metadata = new CommitmentMetadata(_historyColumns, EpochPolicy);
    }

    [TearDown]
    public void TearDown()
    {
        _tier.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Tip_capture_epoch_snapshot(int recentEpochs)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofBuildEnabled = true, ArchiveProofRecentEpochs = recentEpochs };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        ForwardCommitmentCapture capture = new(_historyColumns, EpochPolicy, _metadata, new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance);
        HistoryWriter writer = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance, capture);

        CommitBlock(0, Quiet, LeafRlp(0));
        for (ulong block = 1; block <= 130; block++) CommitBlock(block, Busy, LeafRlp((int)block));

        writer.CaptureUpTo(StateAt(130), _repository, CancellationToken.None);

        bool coverage = _metadata.TryGetCoverage(out ulong from, out ulong to);
        TestContext.Out.WriteLine($"recentEpochs={recentEpochs} retainedFromEpoch={_metadata.RetainedFromEpoch} coverage={coverage} [{from}, {to}]");
        TestContext.Out.WriteLine($"quiet row at or below 130, minEpoch 0   : {Describe(RowAtOrBelow(Quiet, 130, minEpoch: 0))}");
        TestContext.Out.WriteLine($"quiet row at or below 130, minEpoch 1   : {Describe(RowAtOrBelow(Quiet, 130, minEpoch: 1))}");
        TestContext.Out.WriteLine($"quiet row at or below 130, checkpoint   : {Describe(RowAtOrBelow(Quiet, 130, minEpoch: 1, exact: false))}");
        TestContext.Out.WriteLine($"busy  row at or below 130, minEpoch 1   : {Describe(RowAtOrBelow(Busy, 130, minEpoch: 1))}");
    }

    private static string Describe(ulong? suffix) => suffix is null ? "NONE" : $"suffix {suffix}";

    private ulong? RowAtOrBelow(in TreePath path, ulong suffix, ulong minEpoch, bool exact = true)
    {
        CommitmentStore store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments), EpochPolicy, 0);
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, path, exact);
        using CommitmentStore.RowChain chain = store.OpenAtOrBelow(prefix[..prefixLength], exact ? suffix : EpochPolicy.WindowAtOrBelow(suffix), minEpoch: minEpoch);
        return chain.MoveNext() ? chain.CurrentSuffix : null;
    }

    private void CommitBlock(ulong block, in TreePath path, byte[] rlp)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(
            block == 0 ? StateId.PreGenesis : StateAt(block - 1), StateAt(block), ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp);
        Assert.That(_repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
        _repository.AddStateId(StateAt(block));
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
        root[1] = (byte)(blockNumber >> 8);
        return new StateId(blockNumber, new ValueHash256(root));
    }
}
