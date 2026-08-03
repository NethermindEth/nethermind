// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotCompactorTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private static readonly PbtConfig Config = new() { CompactSize = 16 };

    [Test]
    public void Compact_PreservesNewestCanonicalLeafAndNode()
    {
        PbtFullKey key = new([1]);
        PbtFullKey locator = new([2]);
        PbtSnapshotContent older = new(); older.SetLeaf(key, TestItem.KeccakA.ValueHash256); older.SetNode(locator, [1]);
        PbtSnapshotContent newer = new(); newer.SetLeaf(key, TestItem.KeccakB.ValueHash256); newer.SetNode(locator, [2]);
        using PbtSnapshotPooledList chain = new(2);
        chain.Add(new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, older, _pool, PbtResourcePool.Usage.MainBlockProcessing));
        chain.Add(new PbtSnapshot(new StateId(1, default), new StateId(2, default), default, newer, _pool, PbtResourcePool.Usage.MainBlockProcessing));
        using PbtSnapshot compacted = NewCompactor().Compact(chain);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(compacted.Content.TryGetLeaf(key, out ValueHash256? leaf) && leaf == TestItem.KeccakB.ValueHash256, Is.True);
            Assert.That(compacted.Content.TryGetNode(locator, out byte[]? node) && node.SequenceEqual([2]), Is.True);
        }
    }

    private PbtSnapshotCompactor NewCompactor() => new(_pool, new PbtCompactionSchedule(new Nethermind.Db.MemDb(), Config, Nethermind.Logging.LimboLogs.Instance), new PbtSnapshotRepository(), Config);
}
