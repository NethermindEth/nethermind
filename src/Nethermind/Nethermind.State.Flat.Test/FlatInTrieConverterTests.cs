// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatInTrieConverterTests
{
    private static readonly StateId State = new(1, TestItem.KeccakA);
    private static readonly StateId NextState = new(2, TestItem.KeccakB);
    private static readonly Account AccountA = new(1, 100);
    private static readonly Account AccountB = new(2, 200);

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private FlatInTrieConverter _converter = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _converter = new FlatInTrieConverter(_db, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void Convert_MovesTheFlatStateIntoTheTrieColumns()
    {
        TreePath statePath = TreePath.FromHexString("123456");
        byte[] stateNode = Bytes.FromHexString("0xc28080");
        using (IPersistence.IWriteBatch batch = CreateFlatPersistence().CreateWriteBatch(StateId.PreGenesis, State))
        {
            batch.SetAccount(TestItem.AddressA, AccountA);
            batch.SetStorage(TestItem.AddressA, 1, 2);
            batch.SetStateTrieNode(statePath, stateNode);
        }

        _converter.Convert(CancellationToken.None);
        // A rerun after the layout flipped only redoes the cleanup.
        _converter.Convert(CancellationToken.None);

        using IPersistence.IPersistenceReader reader = CreateFlatInTriePersistence().CreateReader();
        UInt256 slot = 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(State));
            Assert.That(reader.GetAccount(TestItem.AddressA), Is.EqualTo(AccountA));
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref slot), Is.True);
            Assert.That(slot, Is.EqualTo((UInt256)2));
            Assert.That(reader.TryLoadStateRlp(statePath, ReadFlags.None), Is.EqualTo(stateNode));
            Assert.That(_db.GetColumnDb(FlatDbColumns.Account).GetAll(), Is.Empty);
            Assert.That(_db.GetColumnDb(FlatDbColumns.Storage).GetAll(), Is.Empty);
            Assert.That(CreateFlatPersistence, Throws.TypeOf<InvalidConfigurationException>(), "the layout should now be FlatInTrie");
        }
    }

    [Test]
    public void Convert_AfterAnInterruptedRun_DropsTheCopiesItLeft()
    {
        using (IPersistence.IWriteBatch batch = CreateFlatPersistence().CreateWriteBatch(StateId.PreGenesis, State))
        {
            batch.SetAccount(TestItem.AddressA, AccountA);
            batch.SetAccount(TestItem.AddressB, AccountB);
        }

        Assert.That(() => _converter.Convert(new CancellationToken(canceled: true)), Throws.InstanceOf<OperationCanceledException>());

        // The copy of account A the interrupted run left, before the node went on, still on Flat, to delete the account.
        byte[] accountAKey = TestItem.AddressA.ToAccountPath.Bytes[..20].ToArray();
        _db.GetColumnDb(FlatDbColumns.StateNodes).PutSpan(accountAKey, _db.GetColumnDb(FlatDbColumns.Account).Get(accountAKey));
        using (IPersistence.IWriteBatch batch = CreateFlatPersistence().CreateWriteBatch(State, NextState))
        {
            batch.SetAccount(TestItem.AddressA, null);
        }

        _converter.Convert(CancellationToken.None);

        using IPersistence.IPersistenceReader reader = CreateFlatInTriePersistence().CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccount(TestItem.AddressA), Is.Null);
            Assert.That(reader.GetAccount(TestItem.AddressB), Is.EqualTo(AccountB));
        }
    }

    [TestCase(null, Description = "Fresh DB, no state")]
    [TestCase(FlatLayout.PreimageFlat)]
    public void Convert_WithoutAFlatState_Refuses(FlatLayout? layout)
    {
        if (layout is not null)
        {
            IPersistence preimage = new PreimageRocksdbPersistence(_db, LimboLogs.Instance, layout.Value, NullTrieNodeLog.Instance);
            preimage.CreateWriteBatch(StateId.PreGenesis, State).Dispose();
        }

        Assert.That(() => _converter.Convert(CancellationToken.None),
            Throws.TypeOf<InvalidConfigurationException>()
                .With.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.ForbiddenOptionValue));
    }

    private IPersistence CreateFlatPersistence() => new RocksDbPersistence(_db, LimboLogs.Instance, NullTrieNodeLog.Instance);

    private IPersistence CreateFlatInTriePersistence() => new FlatInTriePersistence(_db, LimboLogs.Instance, NullTrieNodeLog.Instance);
}
