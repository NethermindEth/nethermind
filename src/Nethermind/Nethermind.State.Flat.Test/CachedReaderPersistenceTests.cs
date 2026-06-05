// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class CachedReaderPersistenceTests
{
    private CancellationTokenSource _cts = null!;
    private IProcessExitSource _processExitSource = null!;

    [SetUp]
    public void SetUp()
    {
        _cts = new CancellationTokenSource();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(_cts.Token);
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    [Test]
    public async Task CreateReader_CachesAccountAndSlotReads()
    {
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        Account account = TestItem.GenerateIndexedAccount(1);
        SlotValue slotValue = new([0x42]);
        FakePersistence inner = new();
        inner.SetAccount(address, account);
        inner.SetSlot(address, slot, found: true, slotValue);

        await using CachedReaderPersistence persistence = new(inner, _processExitSource, LimboLogs.Instance);

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(account));
            SlotValue actualSlot = default;
            Assert.That(reader.TryGetSlot(address, in slot, ref actualSlot), Is.True);
            Assert.That(actualSlot, Is.EqualTo(slotValue));
        }

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(account));
            SlotValue actualSlot = default;
            Assert.That(reader.TryGetSlot(address, in slot, ref actualSlot), Is.True);
            Assert.That(actualSlot, Is.EqualTo(slotValue));
        }

        Assert.That(inner.GetAccountCalls, Is.EqualTo(1));
        Assert.That(inner.TryGetSlotCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateReader_CachesNullAccountAndMissingSlotReads()
    {
        Address address = TestItem.AddressB;
        UInt256 slot = 2;
        FakePersistence inner = new();
        inner.SetAccount(address, null);
        inner.SetSlot(address, slot, found: false, default);

        await using CachedReaderPersistence persistence = new(inner, _processExitSource, LimboLogs.Instance);

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.Null);
            SlotValue actualSlot = new([0x01]);
            Assert.That(reader.TryGetSlot(address, in slot, ref actualSlot), Is.False);
            Assert.That(actualSlot, Is.EqualTo(default(SlotValue)));
        }

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.Null);
            SlotValue actualSlot = new([0x02]);
            Assert.That(reader.TryGetSlot(address, in slot, ref actualSlot), Is.False);
            Assert.That(actualSlot, Is.EqualTo(default(SlotValue)));
        }

        Assert.That(inner.GetAccountCalls, Is.EqualTo(1));
        Assert.That(inner.TryGetSlotCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task WriteBatchDispose_ClearsReadValueCaches()
    {
        Address address = TestItem.AddressC;
        Account firstAccount = TestItem.GenerateIndexedAccount(1);
        Account secondAccount = TestItem.GenerateIndexedAccount(2);
        FakePersistence inner = new();
        inner.SetAccount(address, firstAccount);

        await using CachedReaderPersistence persistence = new(inner, _processExitSource, LimboLogs.Instance);

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(firstAccount));
        }

        inner.SetAccount(address, secondAccount);
        using (persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
        {
        }

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(secondAccount));
        }

        Assert.That(inner.GetAccountCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task ReaderCreatedBeforeWriteDoesNotPopulateNewValueCache()
    {
        Address address = TestItem.AddressD;
        Account firstAccount = TestItem.GenerateIndexedAccount(1);
        Account secondAccount = TestItem.GenerateIndexedAccount(2);
        FakePersistence inner = new();
        inner.SetAccount(address, firstAccount);

        await using CachedReaderPersistence persistence = new(inner, _processExitSource, LimboLogs.Instance);

        using IPersistence.IPersistenceReader oldReader = persistence.CreateReader();
        Assert.That(oldReader.GetAccount(address), Is.EqualTo(firstAccount));

        inner.SetAccount(address, secondAccount);
        using (persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
        {
        }

        Assert.That(oldReader.GetAccount(address), Is.EqualTo(firstAccount));

        using (IPersistence.IPersistenceReader newReader = persistence.CreateReader())
        {
            Assert.That(newReader.GetAccount(address), Is.EqualTo(secondAccount));
        }

        using (IPersistence.IPersistenceReader cachedNewReader = persistence.CreateReader())
        {
            Assert.That(cachedNewReader.GetAccount(address), Is.EqualTo(secondAccount));
        }

        Assert.That(inner.GetAccountCalls, Is.EqualTo(3));
    }

    private sealed class FakePersistence : IPersistence
    {
        private readonly Dictionary<AddressAsKey, Account?> _accounts = [];
        private readonly Dictionary<StorageCell, (bool Found, SlotValue Value)> _slots = [];

        public int GetAccountCalls;
        public int TryGetSlotCalls;

        public void SetAccount(Address address, Account? account) => _accounts[address] = account;

        public void SetSlot(Address address, in UInt256 slot, bool found, SlotValue value) =>
            _slots[new StorageCell(address, in slot)] = (found, value);

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) =>
            new FakePersistenceReader(
                this,
                new Dictionary<AddressAsKey, Account?>(_accounts),
                new Dictionary<StorageCell, (bool Found, SlotValue Value)>(_slots));

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
            new FakeWriteBatch();

        public void Flush() { }

        public void Clear() { }
    }

    private sealed class FakePersistenceReader(
        FakePersistence persistence,
        Dictionary<AddressAsKey, Account?> accounts,
        Dictionary<StorageCell, (bool Found, SlotValue Value)> slots) : IPersistence.IPersistenceReader
    {
        public Account? GetAccount(Address address)
        {
            persistence.GetAccountCalls++;
            return accounts.GetValueOrDefault(address);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            persistence.TryGetSlotCalls++;
            (bool Found, SlotValue Value) value = slots.GetValueOrDefault(new StorageCell(address, in slot));
            outValue = value.Value;
            return value.Found;
        }

        public StateId CurrentState => StateId.PreGenesis;

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            throw new NotSupportedException();

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            throw new NotSupportedException();

        public bool IsPreimageMode => false;

        public void Dispose() { }
    }

    private sealed class FakeWriteBatch : IPersistence.IWriteBatch
    {
        public void SelfDestruct(Address addr) { }
        public void SetAccount(Address addr, Account? account) { }
        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) { }
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? value) { }
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) { }
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) { }
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) { }
        public void Dispose() { }
    }
}
