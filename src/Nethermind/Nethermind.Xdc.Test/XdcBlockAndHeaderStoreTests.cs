// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class XdcBlockAndHeaderStoreTests
{
    IXdcHeaderStore _headerStore;
    IBlockStore _blockStore;

    IDb headerDb;
    IDb blockNumDb;
    IDb blockDb;


    [SetUp]
    public void Setup()
    {
        headerDb = new MemDb();
        blockNumDb = new MemDb();
        blockDb = new MemDb();

        _headerStore = new XdcHeaderStore(headerDb, blockNumDb);
        _blockStore = new XdcBlockStore(blockDb);
    }

    [Test]
    public void XdcHeaderStore_ShouldInheritFromHeaderStore() =>
        Assert.That(_headerStore, Is.InstanceOf<HeaderStore>());

    [Test]
    public void XdcHeaderStore_InsertAndGetHeader_ShouldWorkCorrectly()
    {
        // Arrange
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData();
        XdcBlockHeader header = headerBuilder.TestObject;

        // Act
        _headerStore.Insert(header);
        XdcBlockHeader? retrievedHeader = _headerStore.Get(header.Hash!, false);
        // Assert
        retrievedHeader.Should().BeEquivalentTo(header);
    }

    [Test]
    public void XdcBlockStore_InsertAndGetBlock_ShouldWorkCorrectly()
    {
        // Arrange
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData();
        XdcBlockHeader header = headerBuilder.TestObject;
        BlockBuilder blockBuilder = Build.A.Block.WithHeader(header);
        Block block = blockBuilder.TestObject;
        // Act
        _blockStore.Insert(block);
        Block? retrievedBlock = _blockStore.Get(block.Number, block.Hash!);
        // Assert
        retrievedBlock.Should().BeEquivalentTo(block, options => options.Excluding(h => h.EncodedSize));
    }

    [Test]
    public void XdcBlockHeader_CreateSimulatedChild_ShouldKeepXdcHeaderTypeWithExplicitDefaults()
    {
        XdcBlockHeader parent = Build.A.XdcBlockHeader()
            .WithHash(TestItem.KeccakA)
            .WithGeneratedExtraConsensusData()
            .TestObject;
        parent.Validators = [1, 2, 3];
        parent.Validator = [4, 5, 6];
        parent.Penalties = [7, 8, 9];

        BlockHeader child = parent.CreateSimulatedChild(parent.Timestamp + 12);

        child.Should().BeOfType<XdcBlockHeader>();
        XdcBlockHeader xdcChild = (XdcBlockHeader)child;
        xdcChild.ParentHash.Should().Be(parent.Hash!);
        xdcChild.Number.Should().Be(parent.Number + 1);
        xdcChild.Timestamp.Should().Be(parent.Timestamp + 12);
        xdcChild.ExtraData.Should().BeEmpty();
        xdcChild.MixHash.Should().Be(Hash256.Zero);
        xdcChild.RequestsHash.Should().Be(parent.RequestsHash!);
        xdcChild.Validators.Should().BeNull();
        xdcChild.Validator.Should().BeNull();
        xdcChild.Penalties.Should().BeNull();
        xdcChild.ExtraConsensusData.Should().BeNull();
    }

    [Test]
    public void XdcSubnetBlockHeader_CreateSimulatedChild_ShouldKeepSubnetHeaderType()
    {
        XdcSubnetBlockHeader parent = Build.A.XdcSubnetBlockHeader().WithGeneratedExtraConsensusData().TestObject;
        parent.Hash = TestItem.KeccakA;
        parent.NextValidators = [1, 2, 3];

        BlockHeader child = parent.CreateSimulatedChild(parent.Timestamp + 12);

        child.Should().BeOfType<XdcSubnetBlockHeader>();
        XdcSubnetBlockHeader subnetChild = (XdcSubnetBlockHeader)child;
        subnetChild.ParentHash.Should().Be(parent.Hash);
        subnetChild.Number.Should().Be(parent.Number + 1);
        subnetChild.Timestamp.Should().Be(parent.Timestamp + 12);
        subnetChild.ExtraData.Should().BeEmpty();
        subnetChild.MixHash.Should().Be(Hash256.Zero);
        subnetChild.NextValidators.Should().BeNull();
    }
}
