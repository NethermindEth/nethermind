// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatStateReaderTests
{
    private MemDb _codeDb = null!;
    private IFlatDbManager _flatDbManager = null!;
    private FlatStateReader _reader = null!;
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _codeDb = new MemDb();
        _flatDbManager = Substitute.For<IFlatDbManager>();
        _reader = new FlatStateReader(_codeDb, _flatDbManager, LimboLogs.Instance);
        _pool = new ResourcePool(new FlatDbConfig { CompactSize = 2 });
    }

    [TearDown]
    public void TearDown() => _codeDb.Dispose();

    private ReadOnlySnapshotBundle MakeBundle(System.Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = _pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        Snapshot snap = new(StateId.PreGenesis, StateId.PreGenesis, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        SnapshotPooledList list = new(1);
        list.Add(snap);
        return new ReadOnlySnapshotBundle(list, Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: false);
    }

    [Test]
    public void TryGetAccount_BundleNull_ReturnsFalse()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns((ReadOnlySnapshotBundle)null!);

        bool result = _reader.TryGetAccount(Build.A.BlockHeader.TestObject, TestItem.AddressA, out AccountStruct account);

        result.Should().BeFalse();
        account.Should().Be(default(AccountStruct));
    }

    [Test]
    public void TryGetAccount_FoundInBundle_ReturnsTrue()
    {
        Account expected = TestItem.GenerateIndexedAccount(7);
        ReadOnlySnapshotBundle bundle = MakeBundle(c => c.Accounts[new HashedKey<Address>(TestItem.AddressA)] = expected);
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(bundle);

        bool result = _reader.TryGetAccount(Build.A.BlockHeader.TestObject, TestItem.AddressA, out AccountStruct account);

        result.Should().BeTrue();
        account.Balance.Should().Be(expected.Balance);
        account.Nonce.Should().Be(expected.Nonce);
    }

    [Test]
    public void TryGetAccount_NotFound_ReturnsFalse()
    {
        ReadOnlySnapshotBundle bundle = MakeBundle();
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(bundle);

        bool result = _reader.TryGetAccount(Build.A.BlockHeader.TestObject, TestItem.AddressA, out AccountStruct account);

        result.Should().BeFalse();
        account.Should().Be(default(AccountStruct));
    }

    [Test]
    public void GetStorage_BundleNull_ReturnsEmpty()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns((ReadOnlySnapshotBundle)null!);

        ReadOnlySpan<byte> result = _reader.GetStorage(Build.A.BlockHeader.TestObject, TestItem.AddressA, (UInt256)1);

        result.IsEmpty.Should().BeTrue();
    }

    [Test]
    public void GetStorage_FoundInBundle_ReturnsValue()
    {
        UInt256 slot = 42;
        SlotValue stored = SlotValue.FromSpanWithoutLeadingZero([0xab, 0xcd]);
        ReadOnlySnapshotBundle bundle = MakeBundle(c =>
            c.Storages[new HashedKey<(Address, UInt256)>((TestItem.AddressA, slot))] = stored);
        _flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>()).Returns(bundle);

        byte[] result = _reader.GetStorage(Build.A.BlockHeader.TestObject, TestItem.AddressA, slot).ToArray();

        result.Should().Equal(0xab, 0xcd);
    }

    [Test]
    public void GetCode_EmptyHash_ReturnsEmptyArray()
    {
        _reader.GetCode(Keccak.OfAnEmptyString).Should().BeEmpty();
        _reader.GetCode(Keccak.OfAnEmptyString.ValueHash256).Should().BeEmpty();
    }

    [Test]
    public void GetCode_NonEmptyHash_DelegatesToCodeDb()
    {
        Hash256 hash = TestItem.KeccakA;
        byte[] expected = [0x60, 0x80];
        _codeDb[hash.Bytes] = expected;

        _reader.GetCode(hash).Should().Equal(expected);
        _reader.GetCode(hash.ValueHash256).Should().Equal(expected);
    }

    [Test]
    public void HasStateForBlock_DelegatesToFlatDbManager()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        _flatDbManager.HasStateForBlock(Arg.Any<StateId>()).Returns(true);

        _reader.HasStateForBlock(header).Should().BeTrue();
        _flatDbManager.Received(1).HasStateForBlock(Arg.Any<StateId>());
    }
}
