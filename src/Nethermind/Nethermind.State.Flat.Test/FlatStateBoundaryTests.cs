// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatStateBoundaryTests
{
    [Test]
    public void TryGetBestPersistedState_WhenStatePersisted_ReturnsNumberAndRoot()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        FlatStateBoundary boundary = CreateBoundary(db);
        BasePersistence.SetCurrentState(db.GetColumnDb(FlatDbColumns.Metadata), new StateId(164, TestItem.KeccakA));

        bool found = boundary.TryGetBestPersistedState(out ulong blockNumber, out Hash256? stateRoot);

        Assert.That(found, Is.True);
        Assert.That(blockNumber, Is.EqualTo(164UL));
        Assert.That(stateRoot, Is.EqualTo(TestItem.KeccakA));
        Assert.That(boundary.BestPersistedState, Is.EqualTo(164UL));
    }

    [Test]
    public void TryGetBestPersistedState_WhenNoStatePersisted_ReturnsFalse()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        FlatStateBoundary boundary = CreateBoundary(db);

        Assert.That(boundary.TryGetBestPersistedState(out _, out _), Is.False);
        Assert.That(boundary.BestPersistedState, Is.Null);
    }

    [Test]
    public void TryGetBestPersistedState_WhileSyncing_ReturnsFalse()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        FlatStateBoundary boundary = CreateBoundary(db);
        BasePersistence.SetCurrentState(db.GetColumnDb(FlatDbColumns.Metadata), StateId.Sync);

        Assert.That(boundary.TryGetBestPersistedState(out _, out _), Is.False);
    }

    private static FlatStateBoundary CreateBoundary(SnapshotableMemColumnsDb<FlatDbColumns> db) =>
        new(new RocksDbPersistence(db, LimboLogs.Instance));
}
