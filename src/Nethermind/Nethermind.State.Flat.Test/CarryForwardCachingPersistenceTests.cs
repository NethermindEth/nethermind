// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class CarryForwardCachingPersistenceTests
{
    private static readonly StateId Basis0 = new(0, Keccak.EmptyTreeHash);
    private static readonly StateId Basis1 = new(1, Keccak.EmptyTreeHash);
    private static readonly Address Address = TestItem.AddressA;

    [TestCaseSource(nameof(SlotReadCases))]
    public void TryGetSlot_SecondReadAfterScenario_ReadsInnerExpectedTimes(Action<CarryForwardCachingPersistence, FakePersistence> scenario, int expectedSlotReads)
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);

        ReadSlot(cache, 1);
        scenario(cache, inner);
        ReadSlot(cache, 1);

        Assert.That(inner.SlotReads, Is.EqualTo(expectedSlotReads));
    }

    [Test]
    public void GetAccount_SecondReadAtSameBasis_ServedFromCache()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);

        using (IPersistence.IPersistenceReader reader = cache.CreateReader()) reader.GetAccount(Address);
        using (IPersistence.IPersistenceReader reader = cache.CreateReader()) reader.GetAccount(Address);

        Assert.That(inner.AccountReads, Is.EqualTo(1));
    }

    [Test]
    public void GetAccount_WhenCapacityExceeded_EvictsAllThenReCaches()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner, maxEntriesPerKind: 1);

        ReadAccount(cache, TestItem.AddressA);
        ReadAccount(cache, TestItem.AddressA);
        ReadAccount(cache, TestItem.AddressB);
        ReadAccount(cache, TestItem.AddressA);

        Assert.That(inner.AccountReads, Is.EqualTo(3), "second distinct address overflows capacity 1, clearing the first");
    }

    [Test]
    public void GetSlots_BatchesMissesAndServesSubsequentReadsFromCache()
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);
        StorageCell[] cells =
        [
            new(TestItem.AddressA, (UInt256)1),
            new(TestItem.AddressB, (UInt256)2),
        ];

        ReadSlots(cache, cells);
        ReadSlots(cache, cells);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.SlotMultiGetCalls, Is.EqualTo(1));
            Assert.That(inner.SlotReads, Is.EqualTo(2));
        }
    }

    [Test]
    public void GetSlots_CachedMissingSlotClearsPoisonedOutput()
    {
        FakePersistence inner = new() { SlotFound = false };
        CarryForwardCachingPersistence cache = new(inner);
        StorageCell cell = new(TestItem.AddressA, (UInt256)1);

        using (IPersistence.IPersistenceReader reader = cache.CreateReader())
        {
            UInt256 value = BaseFlatPersistence.DecodeSlotValue([0xff]);
            Assert.That(reader.TryGetSlot(cell.Address, cell.Index, ref value), Is.False);
        }

        UInt256[] values = [BaseFlatPersistence.DecodeSlotValue([0xff])];
        bool[] found = [true];
        using (IPersistence.IPersistenceReader reader = cache.CreateReader())
            reader.GetSlots([cell], values, found);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found[0], Is.False);
            Assert.That(values[0], Is.EqualTo(default(UInt256)));
            Assert.That(inner.SlotMultiGetCalls, Is.Zero);
        }
    }

    [Test]
    public void GetAccounts_WhenGenerationAdvancesDuringBatch_UsesRetainedReaderForWholeRequest()
    {
        FakePersistence inner = new() { SnapshotAwareValues = true };
        CarryForwardCachingPersistence cache = new(inner);
        Address missingAddress = TestItem.AddressB;
        {
            using IPersistence.IPersistenceReader reader = cache.CreateReader();
            reader.GetAccount(Address);
            inner.OnBatchAccountsRead = () =>
            {
                AdvanceBasis(cache);
                inner.ReaderState = Basis1;
                using IPersistence.IPersistenceReader refillReader = cache.CreateReader();
                Account? refillAccount = refillReader.GetAccount(missingAddress);
                Assert.That(refillAccount!.Nonce, Is.EqualTo(2));
            };

            Account?[] accounts = new Account?[2];
            reader.GetAccounts([Address, missingAddress], accounts);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(inner.AccountMultiGetCalls, Is.EqualTo(2));
                Assert.That(inner.BatchAccountKeys[0], Is.EqualTo(new[] { missingAddress }));
                Assert.That(inner.BatchAccountKeys[1], Is.EqualTo(new[] { Address, missingAddress }));
                Assert.That(accounts[0]!.Nonce, Is.EqualTo(1));
                Assert.That(accounts[0]!.Balance, Is.EqualTo((UInt256)100));
                Assert.That(accounts[1]!.Nonce, Is.EqualTo(1));
                Assert.That(accounts[1]!.Balance, Is.EqualTo((UInt256)100));
            }
        }
    }

    [Test]
    public void GetSlots_WhenGenerationAdvancesDuringBatch_UsesRetainedReaderForWholeRequest()
    {
        FakePersistence inner = new() { SnapshotAwareValues = true };
        CarryForwardCachingPersistence cache = new(inner);
        StorageCell cachedCell = new(Address, (UInt256)1);
        StorageCell missingCell = new(Address, (UInt256)2);
        {
            using IPersistence.IPersistenceReader reader = cache.CreateReader();
            UInt256 cachedValue = default;
            Assert.That(reader.TryGetSlot(cachedCell.Address, cachedCell.Index, ref cachedValue), Is.True);
            inner.OnBatchSlotsRead = () =>
            {
                AdvanceBasis(cache);
                inner.ReaderState = Basis1;
                using IPersistence.IPersistenceReader refillReader = cache.CreateReader();
                UInt256 refillValue = default;
                refillReader.TryGetSlot(missingCell.Address, missingCell.Index, ref refillValue);
                Assert.That(refillValue, Is.EqualTo(BaseFlatPersistence.DecodeSlotValue([0x22])));
            };

            UInt256[] slots = [BaseFlatPersistence.DecodeSlotValue([0xff]), BaseFlatPersistence.DecodeSlotValue([0xff])];
            bool[] found = [false, false];
            reader.GetSlots([cachedCell, missingCell], slots, found);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(inner.SlotMultiGetCalls, Is.EqualTo(2));
                Assert.That(inner.BatchSlotKeys[0], Is.EqualTo(new[] { missingCell }));
                Assert.That(inner.BatchSlotKeys[1], Is.EqualTo(new[] { cachedCell, missingCell }));
                Assert.That(found, Is.All.True);
                Assert.That(slots, Is.EqualTo(new[]
                {
                    BaseFlatPersistence.DecodeSlotValue([0x11]),
                    BaseFlatPersistence.DecodeSlotValue([0x11])
                }));
            }
        }
    }

    private static IEnumerable<TestCaseData> SlotReadCases()
    {
        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((_, _) => { }), 1)
        { TestName = "same_basis_served_from_cache" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 2, BaseFlatPersistence.DecodeSlotValue([0x22]));
            inner.ReaderState = Basis1;
        }), 1)
        { TestName = "unwritten_slot_carried_forward" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 1, BaseFlatPersistence.DecodeSlotValue([0x22]));
            inner.ReaderState = Basis1;
        }), 2)
        { TestName = "written_slot_invalidated" };

        yield return ClearingScenario("self_destruct_clears_cache", batch => batch.SelfDestruct(Address));
        yield return ClearingScenario("delete_account_range_clears_cache", batch => batch.DeleteAccountRange(default, default));
        yield return ClearingScenario("delete_storage_range_clears_cache", batch => batch.DeleteStorageRange(default, default, default));
        yield return ClearingScenario("set_account_raw_clears_cache", batch => batch.SetAccountRaw(default, new Account(1, 100)));
        yield return ClearingScenario("set_storage_raw_encoded_clears_cache", batch => batch.SetStorageRawEncoded(default, default, default));

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, _) =>
        {
            // Advance the cache basis but leave the reader behind, so it must bypass the cache.
            using (cache.CreateWriteBatch(Basis0, Basis1)) { }
        }), 2)
        { TestName = "reader_behind_basis_bypasses" };
    }

    private static TestCaseData ClearingScenario(string name, Action<IPersistence.IWriteBatch> write) =>
        new((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                write(batch);
            inner.ReaderState = Basis1;
        }), 2)
        { TestName = name };

    private static void AdvanceBasis(CarryForwardCachingPersistence cache)
    {
        using IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1);
    }

    private static void ReadSlot(IPersistence persistence, UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        UInt256 value = default;
        reader.TryGetSlot(Address, slot, ref value);
    }

    private static void ReadAccount(IPersistence persistence, Address address)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        reader.GetAccount(address);
    }

    private static void ReadSlots(IPersistence persistence, StorageCell[] cells)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        reader.GetSlots(cells, new UInt256[cells.Length], new bool[cells.Length]);
    }

    public sealed class FakePersistence : IPersistence
    {
        public StateId ReaderState = Basis0;
        public int AccountReads;
        public int AccountMultiGetCalls;
        public int SlotReads;
        public int SlotMultiGetCalls;
        public bool SlotFound { get; init; } = true;
        public bool SnapshotAwareValues { get; init; }
        public Action? OnBatchAccountsRead;
        public Action? OnBatchSlotsRead;
        public List<Address[]> BatchAccountKeys { get; } = [];
        public List<StorageCell[]> BatchSlotKeys { get; } = [];

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => new Reader(this);
        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => new FakeWriteBatch();
        public void Flush() { }
        public void Clear() { }

        private sealed class Reader(FakePersistence parent) : IPersistence.IPersistenceReader
        {
            private readonly StateId _readerState = parent.ReaderState;

            public Account? GetAccount(Address address)
            {
                parent.AccountReads++;
                return parent.SnapshotAwareValues && _readerState == Basis1 ? new Account(2, 200) : new Account(1, 100);
            }

            public void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts)
            {
                parent.AccountMultiGetCalls++;
                parent.BatchAccountKeys.Add(addresses.ToArray());
                for (int i = 0; i < addresses.Length; i++)
                    accounts[i] = GetAccount(addresses[i]);

                Action? callback = parent.OnBatchAccountsRead;
                parent.OnBatchAccountsRead = null;
                callback?.Invoke();
            }

            public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
            {
                parent.SlotReads++;
                if (!parent.SlotFound) return false;
                outValue = BaseFlatPersistence.DecodeSlotValue(parent.SnapshotAwareValues && _readerState == Basis1 ? [0x22] : [0x11]);
                return true;
            }

            public void GetSlots(ReadOnlySpan<StorageCell> storageCells, Span<UInt256> slots, Span<bool> found)
            {
                parent.SlotMultiGetCalls++;
                parent.BatchSlotKeys.Add(storageCells.ToArray());
                for (int i = 0; i < storageCells.Length; i++)
                    found[i] = TryGetSlot(storageCells[i].Address, storageCells[i].Index, ref slots[i]);

                Action? callback = parent.OnBatchSlotsRead;
                parent.OnBatchSlotsRead = null;
                callback?.Invoke();
            }

            public StateId CurrentState => _readerState;
            public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
            public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
            public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
            public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
            public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
            public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
            public bool IsPreimageMode => false;
            public void Dispose() { }
        }
    }
}
