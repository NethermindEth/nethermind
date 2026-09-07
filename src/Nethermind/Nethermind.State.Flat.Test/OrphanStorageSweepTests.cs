// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class OrphanStorageSweepTests
{
    private static readonly Address WithStorage = TestItem.AddressA;
    private static readonly Address Absent = TestItem.AddressB;
    private static readonly Address EmptyRoot = TestItem.AddressC;
    private static readonly byte[] Node = [0xC2, 0x80, 0x80];
    private static readonly byte[] EncodedValue = Rlp.Encode(new byte[] { 0x0A }).Bytes;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private IPersistence _persistence = null!;
    private OrphanStorageSweep _sweep = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        IPersistenceManager manager = Substitute.For<IPersistenceManager>();
        manager.When(m => m.RunMaintenance(Arg.Any<Action<IPersistence>>(), Arg.Any<CancellationToken>()))
            .Do(call => call.Arg<Action<IPersistence>>()(_persistence));
        _sweep = new OrphanStorageSweep(_db, manager, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sweep.Dispose();
        _db.Dispose();
    }

    [Test]
    public void Slots_under_an_absent_or_storage_less_account_are_deleted_and_everything_else_stays()
    {
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
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

        bool handledBefore = _sweep.AlreadyHandled;
        OrphanStorageReport report = _sweep.RunToCompletion(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handledBefore, Is.False);
            Assert.That(_sweep.AlreadyHandled, Is.True, "a swept database is stamped so the sweep does not repeat on every start");
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
    public void Two_accounts_sharing_the_four_byte_key_prefix_are_told_apart()
    {
        ValueHash256 live = PathWithPrefix(0x11223344, tail: 0x01);
        ValueHash256 orphan = PathWithPrefix(0x11223344, tail: 0x02);
        ValueHash256 slot = TestItem.KeccakB.ValueHash256;
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetAccountRaw(live, new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
            batch.SetStorageRawEncoded(live, slot, EncodedValue);
            batch.SetStorageTrieNode(new Hash256(live), TreePath.Empty, Node);
            batch.SetStorageRawEncoded(orphan, slot, EncodedValue);
            batch.SetStorageTrieNode(new Hash256(orphan), TreePath.Empty, Node);
        }

        OrphanStorageReport report = _sweep.RunToCompletion(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.OrphanAccounts, Is.EqualTo(1));
            Assert.That(reader.TryGetStorageRaw(live, slot, ref value), Is.True, "the range delete covers the whole prefix bucket and must keep the neighbour whose 16-byte suffix differs");
            Assert.That(reader.TryLoadStorageRlp(new Hash256(live), TreePath.Empty, ReadFlags.None), Is.EqualTo(Node));
            Assert.That(reader.TryGetStorageRaw(orphan, slot, ref value), Is.False);
            Assert.That(reader.TryLoadStorageRlp(new Hash256(orphan), TreePath.Empty, ReadFlags.None), Is.Null);
        }
    }

    [Test]
    public void More_orphaned_accounts_than_one_delete_batch_holds_are_all_deleted()
    {
        const int count = 300;
        ValueHash256 slot = TestItem.KeccakB.ValueHash256;
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            for (int index = 0; index < count; index++) batch.SetStorageRawEncoded(PathWithPrefix((uint)(0x20000000 + index), tail: 0x01), slot, EncodedValue);
        }

        OrphanStorageReport report = _sweep.RunToCompletion(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        SlotValue value = default;
        int remaining = 0;
        for (int index = 0; index < count; index++)
        {
            if (reader.TryGetStorageRaw(PathWithPrefix((uint)(0x20000000 + index), tail: 0x01), slot, ref value)) remaining++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.OrphanAccounts, Is.EqualTo(count));
            Assert.That(remaining, Is.Zero);
        }
    }

    [Test]
    public void A_pass_that_runs_out_of_budget_stops_at_a_prefix_boundary_and_the_next_one_resumes_from_its_cursor()
    {
        ValueHash256 slot = TestItem.KeccakB.ValueHash256;
        ValueHash256 first = PathWithPrefix(0x10000000, tail: 0x01);
        ValueHash256 second = PathWithPrefix(0x20000000, tail: 0x01);
        ValueHash256 third = PathWithPrefix(0x30000000, tail: 0x01);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetStorageRawEncoded(first, slot, EncodedValue);
            batch.SetStorageRawEncoded(first, TestItem.KeccakC.ValueHash256, EncodedValue);
            batch.SetStorageRawEncoded(second, slot, EncodedValue);
            batch.SetStorageRawEncoded(third, slot, EncodedValue);
        }

        bool completed = _sweep.RunOnePass(repair: true, maxSlots: 1, TimeSpan.MaxValue, CancellationToken.None);
        SlotValue value = default;
        bool firstGone;
        bool secondStillThere;
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            firstGone = !reader.TryGetStorageRaw(first, slot, ref value);
            secondStillThere = reader.TryGetStorageRaw(second, slot, ref value);
        }

        bool handledAfterOnePass = _sweep.AlreadyHandled;
        OrphanStorageReport report = _sweep.RunToCompletion(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader after = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.False);
            Assert.That(firstGone, Is.True, "a prefix the pass finished is repaired before the pass yields");
            Assert.That(secondStillThere, Is.True, "a pass yields only at a prefix boundary, so the next prefix is untouched until the next pass");
            Assert.That(handledAfterOnePass, Is.False, "nothing is stamped until the whole column has been seen");
            Assert.That(report.OrphanAccounts, Is.EqualTo(3), "the later passes continue from the cursor rather than rescanning");
            Assert.That(report.SlotsScanned, Is.EqualTo(4));
            Assert.That(after.TryGetStorageRaw(second, slot, ref value), Is.False);
            Assert.That(after.TryGetStorageRaw(third, slot, ref value), Is.False);
            Assert.That(_sweep.AlreadyHandled, Is.True);
        }
    }

    [Test]
    public void A_check_reports_orphans_without_deleting_or_stamping()
    {
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetStorage(Absent, 1, SlotValue.FromSpanWithoutLeadingZero([0x0B]));
        }

        OrphanStorageReport report = _sweep.RunToCompletion(repair: false, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.OrphanSlots, Is.EqualTo(1));
            Assert.That(reader.TryGetSlot(Absent, 1, ref value), Is.True, "a check only counts");
            Assert.That(_sweep.AlreadyHandled, Is.False, "a check does not count as the one-time sweep");
        }
    }

    [Test]
    public void A_clean_database_is_left_alone_and_stamped()
    {
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            batch.SetAccount(WithStorage, new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
            batch.SetStorage(WithStorage, 1, SlotValue.FromSpanWithoutLeadingZero([0x0A]));
        }

        OrphanStorageReport report = _sweep.RunToCompletion(repair: true, CancellationToken.None);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        SlotValue value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.EqualTo(new OrphanStorageReport(SlotsScanned: 1, OrphanSlots: 0, OrphanAccounts: 0, MissingAccounts: 0, EmptyRootAccounts: 0)));
            Assert.That(reader.TryGetSlot(WithStorage, 1, ref value), Is.True);
            Assert.That(_sweep.AlreadyHandled, Is.True);
        }
    }

    private static ValueHash256 PathWithPrefix(uint prefix, byte tail)
    {
        byte[] bytes = new byte[Hash256.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, prefix);
        bytes[4] = tail;
        bytes[^1] = tail;
        return new ValueHash256(bytes);
    }
}
