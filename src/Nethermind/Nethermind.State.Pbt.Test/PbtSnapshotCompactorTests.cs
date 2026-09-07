// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotCompactorTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private static readonly PbtConfig Config = new() { CompactSize = 16 };

    [TestCase(false)]
    [TestCase(true)]
    public void Compact_PreservesNewestCanonicalLeafAndGroupAfterSourcesAreDisposed(bool tombstone)
    {
        PbtFullKey key = new([1]);
        PbtNodePath groupKey = new([], 0);
        TrackingMemoryProvider memoryProvider = new();
        PbtSnapshotContent older = new();
        PbtSnapshotContent newer = new();
        older.SetLeaf(key, TestItem.KeccakA.ValueHash256);
        newer.SetLeaf(key, TestItem.KeccakB.ValueHash256);
        byte[] expected;
        using (RefCountingMemory olderPayload = PbtResourcePoolTests.CreateGroup(memoryProvider, TestItem.KeccakA.ValueHash256))
        using (RefCountingMemory newerPayload = PbtResourcePoolTests.CreateGroup(memoryProvider, TestItem.KeccakB.ValueHash256))
        {
            older.SetNodeGroup(groupKey, olderPayload);
            newer.SetNodeGroup(groupKey, tombstone ? null : newerPayload);
            expected = newerPayload.GetSpan().ToArray();
        }
        PbtSnapshot compacted;
        using (PbtSnapshotPooledList chain = new(2))
        {
            chain.Add(new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, older, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            chain.Add(new PbtSnapshot(new StateId(1, default), new StateId(2, default), default, newer, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            compacted = NewCompactor().Compact(chain);
        }
        using (compacted)
        {
            bool found = compacted.Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload);
            using RefCountingMemory? payloadLease = payload;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(compacted.Content.TryGetLeaf(key, out ValueHash256? leaf) && leaf == TestItem.KeccakB.ValueHash256, Is.True);
                Assert.That(found, Is.True);
                Assert.That(payload?.Memory.ToArray(), Is.EqualTo(tombstone ? null : expected));
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    private PbtSnapshotCompactor NewCompactor() => new(_pool, new PbtCompactionSchedule(new Nethermind.Db.MemDb(), Config, Nethermind.Logging.LimboLogs.Instance), new PbtSnapshotRepository(), Config);
}
