// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.PartialArchive;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.PartialArchive;

/// <summary>
/// End-to-end partial archive behavior at the <see cref="TrieStore"/> level: per-block
/// persistence keeps every historical root readable, the window pruner deletes state that left
/// the retention window, and values that alternate between blocks survive as long as any block
/// inside the window references them.
/// </summary>
public class PartialArchiveStateWindowTests
{
    private sealed class PruneEveryCommitStrategy : IPruningStrategy
    {
        public bool DeleteObsoleteKeys => false;
        public bool ShouldPruneDirtyNode(TrieStoreState state) => true;
        public bool ShouldPrunePersistedNode(TrieStoreState state) => false;
    }

    private sealed class RecordingFinalizedStateProvider : IFinalizedStateProvider
    {
        private readonly Dictionary<ulong, Hash256> _roots = new();
        public ulong FinalizedBlockNumber { get; private set; }

        public void MarkFinalized(ulong blockNumber, Hash256 stateRoot)
        {
            _roots[blockNumber] = stateRoot;
            FinalizedBlockNumber = blockNumber;
        }

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) =>
            _roots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

    [Test]
    public async Task Serves_historical_state_within_window_and_prunes_outside()
    {
        using MemDb stateDb = new();
        NodeStorage nodeStorage = new(stateDb);
        using MemColumnsDb<PartialArchiveColumns> archiveDb = new();
        using PartialArchiveNodeTracker tracker = new(archiveDb, nodeStorage, LimboLogs.Instance);
        RecordingFinalizedStateProvider finalized = new();
        PruningConfig pruningConfig = new()
        {
            TrackPastKeys = false,
            PruningBoundary = 2,
            PruneDelayMilliseconds = 0,
        };

        byte[] slotKey = TestItem.KeccakA.BytesToArray();
        byte[] togglingKey = TestItem.KeccakB.BytesToArray();
        byte[][] toggleValues = [TestItem.KeccakC.BytesToArray(), TestItem.KeccakD.BytesToArray()];
        const ulong chainLength = 40;
        const ulong lastPersistedBlock = chainLength - 2; // finalization lags by PruningBoundary

        Dictionary<ulong, Hash256> roots = new();
        using (TrieStore trieStore = new(
            nodeStorage,
            new PruneEveryCommitStrategy(),
            Persist.EveryNBlock(1),
            finalized,
            pruningConfig,
            LimboLogs.Instance,
            tracker))
        {
            PatriciaTree tree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);
            BlockHeader? baseBlock = null;
            for (ulong i = 1; i <= chainLength; i++)
            {
                using (trieStore.BeginScope(baseBlock))
                {
                    tree.Set(slotKey, ValueFor(i));
                    tree.Set(togglingKey, toggleValues[i % 2]);
                    using (trieStore.BeginBlockCommit(i))
                    {
                        tree.Commit();
                    }

                    roots[i] = tree.RootHash;
                    baseBlock = Build.A.BlockHeader.WithParentOptional(baseBlock).WithStateRoot(tree.RootHash).TestObject;
                }

                finalized.MarkFinalized(i, roots[i]);
                await Task.Yield();
                trieStore.WaitForPruning();
            }

            AssertHistoricalReads(nodeStorage, roots, slotKey, togglingKey, toggleValues, 1, lastPersistedBlock);

            const ulong cutoff = 30;
            Assert.That(tracker.RequestPrune(cutoff), Is.True);
            // Barrier drains the queue, guaranteeing the prune command has executed.
            tracker.OnSnapshotPersisted(tracker.LastSnapshotBlock);

            using (Assert.EnterMultipleScope())
            {
                // Root of block b is superseded at b+1, so it is deletable once b+1 <= cutoff.
                for (ulong i = 1; i < cutoff; i++)
                {
                    Assert.That(RootOnDisk(nodeStorage, roots[i]), Is.False, $"root of block {i} should be pruned");
                }

                for (ulong i = cutoff; i <= lastPersistedBlock; i++)
                {
                    Assert.That(RootOnDisk(nodeStorage, roots[i]), Is.True, $"root of block {i} must stay within the window");
                }

                Assert.That(tracker.OldestRetainedBlock, Is.EqualTo(cutoff));
            }

            AssertHistoricalReads(nodeStorage, roots, slotKey, togglingKey, toggleValues, cutoff, lastPersistedBlock);
        }
    }

    private static byte[] ValueFor(ulong blockNumber) => TestItem.Keccaks[(int)blockNumber].BytesToArray();

    private static bool RootOnDisk(NodeStorage nodeStorage, Hash256 root) =>
        nodeStorage.Get(null, TreePath.Empty, root) is not null;

    /// <summary>Reads through a fresh store (empty caches) so assertions reflect the on-disk state.</summary>
    private static void AssertHistoricalReads(
        NodeStorage nodeStorage,
        Dictionary<ulong, Hash256> roots,
        byte[] slotKey,
        byte[] togglingKey,
        byte[][] toggleValues,
        ulong fromBlock,
        ulong toBlock)
    {
        using TrieStore readStore = new(
            nodeStorage,
            No.Pruning,
            No.Persistence,
            new RecordingFinalizedStateProvider(),
            new PruningConfig { TrackPastKeys = false },
            LimboLogs.Instance);
        PatriciaTree reader = new(readStore.GetTrieStore(null), LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            for (ulong i = fromBlock; i <= toBlock; i++)
            {
                Assert.That(reader.Get(slotKey, roots[i]).ToArray(), Is.EqualTo(ValueFor(i)), $"slot value at block {i}");
                Assert.That(reader.Get(togglingKey, roots[i]).ToArray(), Is.EqualTo(toggleValues[i % 2]), $"toggling value at block {i}");
            }
        }
    }
}
