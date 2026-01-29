// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class PreimageRecordingPersistenceTests
{
    private const int PreimageLookupSize = 12;

    private IPersistence _innerPersistence = null!;
    private MemDb _preimageDb = null!;
    private PreimageRecordingPersistence _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _innerPersistence = Substitute.For<IPersistence>();
        _preimageDb = new MemDb();
        _sut = new PreimageRecordingPersistence(_innerPersistence, _preimageDb);
    }

    [TearDown]
    public void TearDown() => _preimageDb.Dispose();

    [Test]
    public void PassThroughOperations_DelegateToInnerPersistence()
    {
        // CreateReader
        IPersistence.IPersistenceReader expectedReader = Substitute.For<IPersistence.IPersistenceReader>();
        _innerPersistence.CreateReader().Returns(expectedReader);
        _sut.CreateReader().Should().BeSameAs(expectedReader);

        // CreateWriteBatch
        StateId from = StateId.PreGenesis;
        StateId to = new StateId(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);
        using IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None);
        _innerPersistence.Received(1).CreateWriteBatch(from, to, WriteFlags.None);
    }

    [Test]
    public void SetAccount_SetStorage_SelfDestruct_RecordPreimages()
    {
        StateId from = StateId.PreGenesis;
        StateId to = new StateId(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        Account account = TestItem.GenerateIndexedAccount(0);
        UInt256 slot = 42;
        SlotValue? value = SlotValue.FromSpanWithoutLeadingZero([0x01, 0x02, 0x03]);

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetAccount(addressA, account);
            batch.SetStorage(addressA, slot, value);
            batch.SelfDestruct(addressB);
        }

        // Verify inner batch calls
        innerBatch.Received(1).SetAccount(addressA, account);
        innerBatch.Received(1).SetStorage(addressA, slot, Arg.Is<SlotValue?>(v => v != null));
        innerBatch.Received(1).SelfDestruct(addressB);

        // Verify address preimages
        ValueHash256 addressAPath = addressA.ToAccountPath;
        _preimageDb.Get(addressAPath.BytesAsSpan[..PreimageLookupSize]).Should().BeEquivalentTo(addressA.Bytes.ToArray());

        ValueHash256 addressBPath = addressB.ToAccountPath;
        _preimageDb.Get(addressBPath.BytesAsSpan[..PreimageLookupSize]).Should().BeEquivalentTo(addressB.Bytes.ToArray());

        // Verify slot preimage
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        _preimageDb.Get(slotHash.BytesAsSpan[..PreimageLookupSize]).Should().BeEquivalentTo(slot.ToBigEndian());
    }

    [Test]
    public void TrieAndRawOperations_WithoutPreimage_DelegateAsRaw()
    {
        StateId from = StateId.PreGenesis;
        StateId to = new StateId(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        TreePath path = TreePath.FromHexString("1234");
        TrieNode node = new TrieNode(NodeType.Leaf, [0xc1, 0x01]);
        Hash256 addrHash = TestItem.KeccakA;
        Hash256 slotHash = TestItem.KeccakB;
        Account account = TestItem.GenerateIndexedAccount(0);
        SlotValue? value = SlotValue.FromSpanWithoutLeadingZero([0xff]);

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStateTrieNode(path, node);
            batch.SetStorageTrieNode(addrHash, path, node);
            batch.SetStorageRaw(addrHash, slotHash, value);
            batch.SetAccountRaw(addrHash, account);
        }

        // Verify trie operations delegated
        innerBatch.Received(1).SetStateTrieNode(path, node);
        innerBatch.Received(1).SetStorageTrieNode(addrHash, path, node);

        // Without preimage, raw operations stay raw
        innerBatch.Received(1).SetStorageRaw(addrHash, slotHash, Arg.Is<SlotValue?>(v => v != null));
        innerBatch.Received(1).SetAccountRaw(addrHash, account);

        // No preimages should be recorded for trie/raw operations
        _preimageDb.Keys.Should().BeEmpty();
    }

    [Test]
    public void RawOperations_WithPreimage_TranslatedToNonRaw()
    {
        Address address = TestItem.AddressA;
        UInt256 slot = 42;
        Account account = TestItem.GenerateIndexedAccount(0);
        SlotValue? value = SlotValue.FromSpanWithoutLeadingZero([0xff]);

        // Pre-populate preimage database with address and slot preimages
        ValueHash256 addrHash = address.ToAccountPath;
        _preimageDb.Set(addrHash.BytesAsSpan[..PreimageLookupSize], address.Bytes.ToArray());

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        _preimageDb.Set(slotHash.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());

        StateId from = StateId.PreGenesis;
        StateId to = new StateId(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStorageRaw(new Hash256(addrHash), new Hash256(slotHash), value);
            batch.SetAccountRaw(new Hash256(addrHash), account);
        }

        // With preimage available, raw operations are translated to non-raw
        innerBatch.Received(1).SetStorage(address, slot, Arg.Is<SlotValue?>(v => v != null));
        innerBatch.Received(1).SetAccount(address, account);

        // Raw operations should NOT be called
        innerBatch.DidNotReceive().SetStorageRaw(Arg.Any<Hash256>(), Arg.Any<Hash256>(), Arg.Any<SlotValue?>());
        innerBatch.DidNotReceive().SetAccountRaw(Arg.Any<Hash256>(), Arg.Any<Account>());
    }

    [Test]
    public void SetStorageRaw_WithOnlyAddressPreimage_FallsBackToRaw()
    {
        Address address = TestItem.AddressA;
        UInt256 slot = 42;
        SlotValue? value = SlotValue.FromSpanWithoutLeadingZero([0xff]);

        // Pre-populate only address preimage (missing slot preimage)
        ValueHash256 addrHash = address.ToAccountPath;
        _preimageDb.Set(addrHash.BytesAsSpan[..PreimageLookupSize], address.Bytes.ToArray());

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        // Note: NOT setting slot preimage

        StateId from = StateId.PreGenesis;
        StateId to = new StateId(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStorageRaw(new Hash256(addrHash), new Hash256(slotHash), value);
        }

        // Without slot preimage, storage stays raw
        innerBatch.Received(1).SetStorageRaw(new Hash256(addrHash), new Hash256(slotHash), Arg.Is<SlotValue?>(v => v != null));
        innerBatch.DidNotReceive().SetStorage(Arg.Any<Address>(), Arg.Any<UInt256>(), Arg.Any<SlotValue?>());
    }

    [Test]
    public void Dispose_DisposesPreimageBatchAndInnerBatch()
    {
        StateId from = StateId.PreGenesis;
        StateId to = new StateId(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None);
        batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        batch.Dispose();

        innerBatch.Received(1).Dispose();

        // Preimages should be flushed after dispose
        ValueHash256 addressPath = TestItem.AddressA.ToAccountPath;
        _preimageDb.Get(addressPath.BytesAsSpan[..PreimageLookupSize]).Should().NotBeNull();
    }
}
