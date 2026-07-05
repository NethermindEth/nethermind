// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class GetCurrentStateTests
{
    [Test]
    public void GetCurrentState_AfterWrite_MatchesReaderView()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance);
        StateId persisted = new(164, TestItem.KeccakA);
        BasePersistence.SetCurrentState(db.GetColumnDb(FlatDbColumns.Metadata), persisted);

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();

        Assert.That(persistence.GetCurrentState(), Is.EqualTo(persisted));
        Assert.That(persistence.GetCurrentState(), Is.EqualTo(reader.CurrentState));
    }

    [Test]
    public void GetCurrentState_WhenNothingPersisted_ReturnsPreGenesisSentinel()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance);

        Assert.That(persistence.GetCurrentState().BlockNumber, Is.EqualTo(ulong.MaxValue));
    }
}
