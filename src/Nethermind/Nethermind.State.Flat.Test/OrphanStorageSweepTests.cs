// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class OrphanStorageSweepTests
{
    private static readonly Address WithStorage = TestItem.AddressA;
    private static readonly Address Absent = TestItem.AddressB;
    private static readonly Address EmptyRoot = TestItem.AddressC;
    private static readonly byte[] Node = [0xC2, 0x80, 0x80];

    [Test]
    public void Slots_under_an_absent_or_storage_less_account_are_deleted_and_everything_else_stays()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = new RocksDbPersistence(db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetAccount(WithStorage, new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
            batch.SetStorage(WithStorage, 1, SlotValue.FromSpanWithoutLeadingZero([0x0A]));
            batch.SetStorageTrieNode(WithStorage.ToAccountPath.ToCommitment(), TreePath.Empty, Node);

            batch.SetStorage(Absent, 1, SlotValue.FromSpanWithoutLeadingZero([0x0B]));

            batch.SetAccount(EmptyRoot, new Account(0, 0));
            batch.SetStorage(EmptyRoot, 1, SlotValue.FromSpanWithoutLeadingZero([0x0C]));
            batch.SetStorage(EmptyRoot, 2, SlotValue.FromSpanWithoutLeadingZero([0x0D]));
            batch.SetStorageTrieNode(EmptyRoot.ToAccountPath.ToCommitment(), TreePath.Empty, Node);
        }

        OrphanStorageSweep sweep = new(db, persistence, LimboLogs.Instance);
        bool sweptBefore = sweep.AlreadySwept;
        OrphanStorageReport report = sweep.Sweep(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sweptBefore, Is.False);
            Assert.That(sweep.AlreadySwept, Is.True, "a swept database is stamped so the sweep does not repeat on every start");
            Assert.That(report, Is.EqualTo(new OrphanStorageReport(SlotsScanned: 4, OrphanSlots: 3, OrphanAccounts: 2, MissingAccounts: 1, EmptyRootAccounts: 1)));
            Assert.That(reader.TryGetSlot(WithStorage, 1, ref value), Is.True, "an account whose storage root says it has storage keeps its slots");
            Assert.That(reader.TryLoadStorageRlp(WithStorage.ToAccountPath.ToCommitment(), TreePath.Empty, ReadFlags.None), Is.EqualTo(Node));
            Assert.That(reader.TryGetSlot(Absent, 1, ref value), Is.False, "a slot under an account that does not exist cannot be part of any state");
            Assert.That(reader.TryGetSlot(EmptyRoot, 1, ref value), Is.False, "a slot under an account with an empty storage root contradicts that root; this is what a contract created and destroyed in one block left behind");
            Assert.That(reader.TryGetSlot(EmptyRoot, 2, ref value), Is.False);
            Assert.That(reader.TryLoadStorageRlp(EmptyRoot.ToAccountPath.ToCommitment(), TreePath.Empty, ReadFlags.None), Is.Null, "its storage trie nodes go with the slots");
            Assert.That(reader.GetAccount(EmptyRoot), Is.Not.Null, "the account itself is untouched");
        }
    }

    [Test]
    public void A_check_reports_orphans_without_deleting_or_stamping()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = new RocksDbPersistence(db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetStorage(Absent, 1, SlotValue.FromSpanWithoutLeadingZero([0x0B]));
        }

        OrphanStorageSweep sweep = new(db, persistence, LimboLogs.Instance);
        OrphanStorageReport report = sweep.Sweep(repair: false, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.OrphanSlots, Is.EqualTo(1));
            Assert.That(reader.TryGetSlot(Absent, 1, ref value), Is.True, "a check only counts");
            Assert.That(sweep.AlreadySwept, Is.False, "a check does not count as the one-time sweep");
        }
    }

    [Test]
    public void A_clean_database_is_left_alone_and_stamped()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = new RocksDbPersistence(db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetAccount(WithStorage, new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
            batch.SetStorage(WithStorage, 1, SlotValue.FromSpanWithoutLeadingZero([0x0A]));
        }

        OrphanStorageSweep sweep = new(db, persistence, LimboLogs.Instance);
        OrphanStorageReport report = sweep.Sweep(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.EqualTo(new OrphanStorageReport(SlotsScanned: 1, OrphanSlots: 0, OrphanAccounts: 0, MissingAccounts: 0, EmptyRootAccounts: 0)));
            Assert.That(reader.TryGetSlot(WithStorage, 1, ref value), Is.True);
            Assert.That(sweep.AlreadySwept, Is.True);
        }
    }
}
