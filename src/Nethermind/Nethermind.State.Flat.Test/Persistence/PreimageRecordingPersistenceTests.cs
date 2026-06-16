// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
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
        Assert.That(_sut.CreateReader(), Is.SameAs(expectedReader));

        // CreateWriteBatch
        StateId from = StateId.PreGenesis;
        StateId to = new(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);
        using IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None);
        _innerPersistence.Received(1).CreateWriteBatch(from, to, WriteFlags.None);
    }

    [Test]
    public void SetAccount_SetStorage_SelfDestruct_RecordPreimages()
    {
        StateId from = StateId.PreGenesis;
        StateId to = new(1, TestItem.KeccakA);
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
        Assert.That(_preimageDb.Get(addressAPath.BytesAsSpan[..PreimageLookupSize]), Is.EqualTo(addressA.Bytes.ToArray()));

        ValueHash256 addressBPath = addressB.ToAccountPath;
        Assert.That(_preimageDb.Get(addressBPath.BytesAsSpan[..PreimageLookupSize]), Is.EqualTo(addressB.Bytes.ToArray()));

        // Verify slot preimage
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        Assert.That(_preimageDb.Get(slotHash.BytesAsSpan[..PreimageLookupSize]), Is.EqualTo(slot.ToBigEndian()));
    }

    [Test]
    public void TrieAndRawOperations_WithoutPreimage_DelegateAsRaw()
    {
        StateId from = StateId.PreGenesis;
        StateId to = new(1, TestItem.KeccakA);
        FakeWriteBatch innerBatch = new();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        TreePath path = TreePath.FromHexString("1234");
        byte[] rlp = [0xc1, 0x01];
        TrieNode node = new(NodeType.Leaf, rlp);
        Hash256 addrHash = TestItem.KeccakA;
        Hash256 slotHash = TestItem.KeccakB;
        Account account = TestItem.GenerateIndexedAccount(0);
        byte[] rlpValue = Rlp.Encode((ReadOnlySpan<byte>)new byte[] { 0xff }).Bytes;

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStateTrieNode(path, node.FullRlp.AsSpan());
            batch.SetStorageTrieNode(addrHash, path, node.FullRlp.AsSpan());
            batch.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
            batch.SetAccountRaw(addrHash, account);
        }

        // Verify trie operations delegated
        Assert.That(innerBatch.SetStateTrieNodeCalls, Has.One.EqualTo((path, rlp)));
        Assert.That(innerBatch.SetStorageTrieNodeCalls, Has.One.EqualTo((addrHash, path, rlp)));

        // Without preimage, raw operations stay raw
        ValueHash256 addrHashValue = addrHash.ValueHash256;
        ValueHash256 slotHashValue = slotHash.ValueHash256;
        Assert.That(innerBatch.SetStorageRawEncodedCalls, Has.One.Matches<(ValueHash256 AddrHash, ValueHash256 SlotHash, byte[] RlpValue)>(c =>
            c.AddrHash == addrHashValue && c.SlotHash == slotHashValue && c.RlpValue.SequenceEqual(rlpValue)));
        Assert.That(innerBatch.SetAccountRawCalls, Has.One.EqualTo((addrHashValue, account)));

        // No preimages should be recorded for trie/raw operations
        Assert.That(_preimageDb.Keys, Is.Empty);
    }

    [Test]
    public void RawOperations_WithPreimage_TranslatedToNonRaw()
    {
        Address address = TestItem.AddressA;
        UInt256 slot = 42;
        Account account = TestItem.GenerateIndexedAccount(0);
        byte[] rlpValue = Rlp.Encode((ReadOnlySpan<byte>)new byte[] { 0xff }).Bytes;

        // Pre-populate preimage database with address and slot preimages
        ValueHash256 addrHash = address.ToAccountPath;
        _preimageDb.Set(addrHash.BytesAsSpan[..PreimageLookupSize], address.Bytes.ToArray());

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        _preimageDb.Set(slotHash.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());

        StateId from = StateId.PreGenesis;
        StateId to = new(1, TestItem.KeccakA);
        FakeWriteBatch innerBatch = new();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStorageRawEncoded(new Hash256(addrHash), new Hash256(slotHash), rlpValue);
            batch.SetAccountRaw(new Hash256(addrHash), account);
        }

        // With preimage available, raw operations are translated to non-raw
        Assert.That(innerBatch.SetStorageCalls, Has.One.Matches<(Address Addr, UInt256 Slot, SlotValue? Value)>(c =>
            c.Addr == address && c.Slot == slot && c.Value is not null));
        Assert.That(innerBatch.SetAccountCalls, Has.One.Matches<(Address Addr, Account? Account)>(c =>
            c.Addr == address && c.Account == account));

        // Raw operations should NOT be called
        Assert.That(innerBatch.SetStorageRawEncodedCalls, Is.Empty);
        Assert.That(innerBatch.SetAccountRawCalls, Is.Empty);
    }

    [Test]
    public void SetStorageRawEncoded_WithOnlyAddressPreimage_FallsBackToRaw()
    {
        Address address = TestItem.AddressA;
        UInt256 slot = 42;
        byte[] rlpValue = Rlp.Encode((ReadOnlySpan<byte>)new byte[] { 0xff }).Bytes;

        // Pre-populate only address preimage (missing slot preimage)
        ValueHash256 addrHash = address.ToAccountPath;
        _preimageDb.Set(addrHash.BytesAsSpan[..PreimageLookupSize], address.Bytes.ToArray());

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        // Note: NOT setting slot preimage

        StateId from = StateId.PreGenesis;
        StateId to = new(1, TestItem.KeccakA);
        FakeWriteBatch innerBatch = new();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        using (IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStorageRawEncoded(new Hash256(addrHash), new Hash256(slotHash), rlpValue);
        }

        // Without slot preimage, storage stays raw (encoded)
        Assert.That(innerBatch.SetStorageRawEncodedCalls, Has.One.Matches<(ValueHash256 AddrHash, ValueHash256 SlotHash, byte[] RlpValue)>(c =>
            c.AddrHash == addrHash && c.SlotHash == slotHash && c.RlpValue.SequenceEqual(rlpValue)));
        Assert.That(innerBatch.SetStorageCalls, Is.Empty);
    }

    [Test]
    public void Dispose_DisposesPreimageBatchAndInnerBatch()
    {
        StateId from = StateId.PreGenesis;
        StateId to = new(1, TestItem.KeccakA);
        IPersistence.IWriteBatch innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        IPersistence.IWriteBatch batch = _sut.CreateWriteBatch(from, to, WriteFlags.None);
        batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        batch.Dispose();

        innerBatch.Received(1).Dispose();

        // Preimages should be flushed after dispose
        ValueHash256 addressPath = TestItem.AddressA.ToAccountPath;
        Assert.That(_preimageDb.Get(addressPath.BytesAsSpan[..PreimageLookupSize]), Is.Not.Null);
    }
}
