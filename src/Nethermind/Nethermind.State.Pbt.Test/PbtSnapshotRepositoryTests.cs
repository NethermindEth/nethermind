// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotRepositoryTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());

    [Test]
    public void BaseSnapshotMetrics_FollowRepositoryOwnership_ByPartitionAndValueType()
    {
        PbtSnapshotRepository repository = new();
        PbtSnapshotPayloadSize baseline = MetricPayloadSize();
        long countBaseline = Metrics.PbtBaseSnapshotCount;
        StateId state = new(1, default);

        try
        {
            Assert.That(repository.TryAdd(Snapshot(state, ContentWithEveryPartition())), Is.True);
            AssertMetrics(countBaseline + 1, baseline, new PbtSnapshotPayloadSize(1, 2, 3, 4, 5, 6));

            Assert.That(repository.TryAdd(Snapshot(state, new PbtSnapshotContent())), Is.False, "a duplicate base snapshot is rejected");
            AssertMetrics(countBaseline + 1, baseline, new PbtSnapshotPayloadSize(1, 2, 3, 4, 5, 6));

            Assert.That(repository.TryAddCompacted(Snapshot(state, ContentWithEveryPartition())), Is.True);
            AssertMetrics(countBaseline + 1, baseline, new PbtSnapshotPayloadSize(1, 2, 3, 4, 5, 6));

            repository.RemoveStatesUntil(state.BlockNumber);
            AssertMetrics(countBaseline, baseline, default);
        }
        finally
        {
            repository.RemoveStatesUntil(ulong.MaxValue);
        }
    }

    private PbtSnapshot Snapshot(in StateId state, PbtSnapshotContent content) =>
        new(StateId.PreGenesis, state, PbtPartitionRoots.Empty, content, _pool, PbtResourcePool.Usage.MainBlockProcessing);

    private static PbtSnapshotContent ContentWithEveryPartition()
    {
        PbtSnapshotContent content = new();
        AddPartitionContent(content, Stem("0x00000000000000000000000000000000000000000000000000000000000001"), 1, 2);
        AddPartitionContent(content, Stem("0x10000000000000000000000000000000000000000000000000000000000001"), 3, 4);
        AddPartitionContent(content, Stem("0x80000000000000000000000000000000000000000000000000000000000001"), 5, 6);
        return content;
    }

    private static void AddPartitionContent(PbtSnapshotContent content, in Stem stem, int leafSize, int trieSize)
    {
        content.SetLeafBlob(stem, RefCountingMemory.Wrapping(new byte[leafSize]));
        content.SetTrieNode(TrieNodeKey.For(8, stem), RefCountingMemory.Wrapping(new byte[trieSize]));

        byte[] tombstoneBytes = stem.Bytes.ToArray();
        tombstoneBytes[^1]++;
        Stem tombstoneStem = new(tombstoneBytes);
        content.SetLeafBlob(tombstoneStem, null);
        content.SetTrieNode(TrieNodeKey.For(16, tombstoneStem), null);
    }

    private static Stem Stem(string hex) => new(Bytes.FromHexString(hex));

    private static PbtSnapshotPayloadSize MetricPayloadSize() => new(
        Metrics.PbtBaseSnapshotMemory[Metrics.AccountLeafSnapshotMemory],
        Metrics.PbtBaseSnapshotMemory[Metrics.AccountTrieSnapshotMemory],
        Metrics.PbtBaseSnapshotMemory[Metrics.CodeLeafSnapshotMemory],
        Metrics.PbtBaseSnapshotMemory[Metrics.CodeTrieSnapshotMemory],
        Metrics.PbtBaseSnapshotMemory[Metrics.StorageLeafSnapshotMemory],
        Metrics.PbtBaseSnapshotMemory[Metrics.StorageTrieSnapshotMemory]);

    private static void AssertMetrics(long expectedCount, in PbtSnapshotPayloadSize baseline, in PbtSnapshotPayloadSize delta)
    {
        PbtSnapshotPayloadSize actual = MetricPayloadSize();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.PbtBaseSnapshotCount, Is.EqualTo(expectedCount), "base snapshot count");
            Assert.That(actual.AccountLeaf, Is.EqualTo(baseline.AccountLeaf + delta.AccountLeaf), "account leaf bytes");
            Assert.That(actual.AccountTrie, Is.EqualTo(baseline.AccountTrie + delta.AccountTrie), "account trie bytes");
            Assert.That(actual.CodeLeaf, Is.EqualTo(baseline.CodeLeaf + delta.CodeLeaf), "code leaf bytes");
            Assert.That(actual.CodeTrie, Is.EqualTo(baseline.CodeTrie + delta.CodeTrie), "code trie bytes");
            Assert.That(actual.StorageLeaf, Is.EqualTo(baseline.StorageLeaf + delta.StorageLeaf), "storage leaf bytes");
            Assert.That(actual.StorageTrie, Is.EqualTo(baseline.StorageTrie + delta.StorageTrie), "storage trie bytes");
        }
    }
}
