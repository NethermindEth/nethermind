// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
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
    public void TearDown()
    {
        _preimageDb.Dispose();
    }

    [Test]
    public void PassThroughOperations_DelegateToInnerPersistence()
    {
        // CreateReader
        var expectedReader = Substitute.For<IPersistence.IPersistenceReader>();
        _innerPersistence.CreateReader().Returns(expectedReader);
        _sut.CreateReader().Should().BeSameAs(expectedReader);

        // WarmUpWhole
        using var cts = new CancellationTokenSource();
        _innerPersistence.WarmUpWhole(cts.Token).Returns(true);
        _sut.WarmUpWhole(cts.Token).Should().BeTrue();

        // SupportConcurrentWrites
        _innerPersistence.SupportConcurrentWrites.Returns(true);
        _sut.SupportConcurrentWrites.Should().BeTrue();
        _innerPersistence.SupportConcurrentWrites.Returns(false);
        _sut.SupportConcurrentWrites.Should().BeFalse();

        // CreateWriteBatch
        var from = StateId.PreGenesis;
        var to = new StateId(1, TestItem.KeccakA);
        var innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);
        using var batch = _sut.CreateWriteBatch(from, to, WriteFlags.None);
        _innerPersistence.Received(1).CreateWriteBatch(from, to, WriteFlags.None);
    }

    [Test]
    public void SetAccount_SetStorage_SelfDestruct_RecordPreimages()
    {
        var from = StateId.PreGenesis;
        var to = new StateId(1, TestItem.KeccakA);
        var innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        innerBatch.SelfDestruct(Arg.Any<Address>()).Returns(5);
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        Account account = TestItem.GenerateIndexedAccount(0);
        UInt256 slot = 42;
        SlotValue? value = SlotValue.FromSpanWithoutLeadingZero([0x01, 0x02, 0x03]);

        using (var batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetAccount(addressA, account);
            batch.SetStorage(addressA, slot, value);
            int result = batch.SelfDestruct(addressB);
            result.Should().Be(5);
        }

        // Verify inner batch calls
        innerBatch.Received(1).SetAccount(addressA, account);
        innerBatch.Received(1).SetStorage(addressA, slot, Arg.Is<SlotValue?>(v => v != null));
        innerBatch.Received(1).SelfDestruct(addressB);

        // Verify address preimages
        ValueHash256 addressAPath = addressA.ToAccountPath;
        _preimageDb.Get(addressAPath.BytesAsSpan[..PreimageLookupSize]).Should().BeEquivalentTo(addressA.Bytes);

        ValueHash256 addressBPath = addressB.ToAccountPath;
        _preimageDb.Get(addressBPath.BytesAsSpan[..PreimageLookupSize]).Should().BeEquivalentTo(addressB.Bytes);

        // Verify slot preimage
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        _preimageDb.Get(slotHash.BytesAsSpan[..PreimageLookupSize]).Should().BeEquivalentTo(slot.ToBigEndian());
    }

    [Test]
    public void TrieAndRawOperations_DelegateWithoutRecordingPreimages()
    {
        var from = StateId.PreGenesis;
        var to = new StateId(1, TestItem.KeccakA);
        var innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        TreePath path = TreePath.FromHexString("1234");
        TrieNode node = new TrieNode(NodeType.Leaf, [0xc1, 0x01]);
        Hash256 addrHash = TestItem.KeccakA;
        Hash256 slotHash = TestItem.KeccakB;
        Account account = TestItem.GenerateIndexedAccount(0);
        SlotValue? value = SlotValue.FromSpanWithoutLeadingZero([0xff]);

        using (var batch = _sut.CreateWriteBatch(from, to, WriteFlags.None))
        {
            batch.SetStateTrieNode(path, node);
            batch.SetStorageTrieNode(addrHash, path, node);
            batch.SetStorageRaw(addrHash, slotHash, value);
            batch.SetAccountRaw(addrHash, account);
        }

        // Verify all inner batch calls
        innerBatch.Received(1).SetStateTrieNode(path, node);
        innerBatch.Received(1).SetStorageTrieNode(addrHash, path, node);
        innerBatch.Received(1).SetStorageRaw(addrHash, slotHash, Arg.Is<SlotValue?>(v => v != null));
        innerBatch.Received(1).SetAccountRaw(addrHash, account);

        // No preimages should be recorded for trie/raw operations
        _preimageDb.Keys.Should().BeEmpty();
    }

    [Test]
    public void Dispose_DisposesPreimageBatchAndInnerBatch()
    {
        var from = StateId.PreGenesis;
        var to = new StateId(1, TestItem.KeccakA);
        var innerBatch = Substitute.For<IPersistence.IWriteBatch>();
        _innerPersistence.CreateWriteBatch(from, to, WriteFlags.None).Returns(innerBatch);

        var batch = _sut.CreateWriteBatch(from, to, WriteFlags.None);
        batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        batch.Dispose();

        innerBatch.Received(1).Dispose();

        // Preimages should be flushed after dispose
        ValueHash256 addressPath = TestItem.AddressA.ToAccountPath;
        _preimageDb.Get(addressPath.BytesAsSpan[..PreimageLookupSize]).Should().NotBeNull();
    }
}
