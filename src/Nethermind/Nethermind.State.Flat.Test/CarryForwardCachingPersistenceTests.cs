// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    public void Read_CommitAndRefillDuringCacheLookup_ReturnsPinnedValue([Values] bool accountRead)
    {
        FakePersistence inner = new();
        CarryForwardCachingPersistence cache = new(inner);
        Action? duringLookup = null;
        if (accountRead)
            ReplaceCacheComparer(cache, "_accounts", new CallbackComparer<Address>(() => duringLookup?.Invoke()));
        else
            ReplaceCacheComparer(cache, "_slots", new CallbackComparer<(Address, UInt256)>(() => duringLookup?.Invoke()));

        using IPersistence.IPersistenceReader oldReader = cache.CreateReader();
        object expected = ReadValue(oldReader);
        object? refilled = null;
        duringLookup = () =>
        {
            duringLookup = null;
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
            {
                batch.SetAccount(Address, new Account(2, 200));
                batch.SetStorage(Address, 1, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x22")));
            }
            inner.ReaderState = Basis1;
            inner.Account = new Account(2, 200);
            inner.Slot = SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x22"));
            using IPersistence.IPersistenceReader newReader = cache.CreateReader();
            refilled = ReadValue(newReader);
        };

        object actual = ReadValue(oldReader);
        using IPersistence.IPersistenceReader latestReader = cache.CreateReader();
        object latest = ReadValue(latestReader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refilled, Is.Not.Null, "the commit must run inside the dictionary lookup");
            Assert.That(refilled, Is.Not.EqualTo(expected));
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(latest, Is.EqualTo(refilled), "the old reader must not populate the new generation");
        }

        object ReadValue(IPersistence.IPersistenceReader reader)
        {
            if (accountRead) return reader.GetAccount(Address)!;
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(Address, 1, ref value), Is.True);
            return value.ToEvmBytes();
        }
    }

    private static void ReplaceCacheComparer<TKey>(CarryForwardCachingPersistence cache, string fieldName, IEqualityComparer<TKey> comparer)
    {
        FieldInfo field = typeof(CarryForwardCachingPersistence).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(cache, Activator.CreateInstance(field.FieldType, [comparer]));
    }

    private sealed class CallbackComparer<TKey>(Action duringLookup) : IEqualityComparer<TKey> where TKey : notnull
    {
        public bool Equals(TKey? x, TKey? y) => EqualityComparer<TKey>.Default.Equals(x, y);

        public int GetHashCode(TKey key)
        {
            // Hashing runs after the reader's generation check but before the dictionary returns its value.
            duringLookup();
            return EqualityComparer<TKey>.Default.GetHashCode(key);
        }
    }

    private static IEnumerable<TestCaseData> SlotReadCases()
    {
        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((_, _) => { }), 1)
        { TestName = "same_basis_served_from_cache" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 2, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            inner.ReaderState = Basis1;
        }), 1)
        { TestName = "unwritten_slot_carried_forward" };

        yield return new TestCaseData((Action<CarryForwardCachingPersistence, FakePersistence>)((cache, inner) =>
        {
            using (IPersistence.IWriteBatch batch = cache.CreateWriteBatch(Basis0, Basis1))
                batch.SetStorage(Address, 1, SlotValue.FromSpanWithoutLeadingZero([0x22]));
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

    private static void ReadSlot(IPersistence persistence, UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue value = default;
        reader.TryGetSlot(Address, slot, ref value);
    }

    private static void ReadAccount(IPersistence persistence, Address address)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        reader.GetAccount(address);
    }

    public sealed class FakePersistence : IPersistence
    {
        public StateId ReaderState = Basis0;
        public Account Account = new(1, 100);
        public SlotValue Slot = SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x11"));
        public int AccountReads;
        public int SlotReads;

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => new Reader(this);
        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => new FakeWriteBatch();
        public void Flush() { }
        public void Clear() { }

        private sealed class Reader(FakePersistence parent) : IPersistence.IPersistenceReader
        {
            private readonly StateId _state = parent.ReaderState;
            private readonly Account _account = parent.Account;
            private readonly SlotValue _slot = parent.Slot;

            public Account? GetAccount(Address address)
            {
                parent.AccountReads++;
                return _account;
            }

            public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
            {
                parent.SlotReads++;
                outValue = _slot;
                return true;
            }

            public StateId CurrentState => _state;
            public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;
            public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
            public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
            public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;
            public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
            public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
            public bool IsPreimageMode => false;
            public void Dispose() { }
        }
    }
}
