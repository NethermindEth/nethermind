// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        Assert.That(retrievedHeader, Is.EqualTo(header).UsingXdcComparer());
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
        Assert.That(retrievedBlock, Is.EqualTo(block).UsingXdcComparer());
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

        Assert.That(child, Is.TypeOf<XdcBlockHeader>());
        XdcBlockHeader xdcChild = (XdcBlockHeader)child;
        Assert.Multiple(() =>
        {
            Assert.That(xdcChild.ParentHash, Is.EqualTo(parent.Hash!));
            Assert.That(xdcChild.Number, Is.EqualTo(parent.Number + 1));
            Assert.That(xdcChild.Timestamp, Is.EqualTo(parent.Timestamp + 12));
            Assert.That(xdcChild.ExtraData, Is.Empty);
            Assert.That(xdcChild.MixHash, Is.EqualTo(Hash256.Zero));
            Assert.That(xdcChild.RequestsHash, Is.EqualTo(parent.RequestsHash!));
            Assert.That(xdcChild.Validators, Is.Null);
            Assert.That(xdcChild.Validator, Is.Null);
            Assert.That(xdcChild.Penalties, Is.Null);
            Assert.That(xdcChild.ExtraConsensusData, Is.Null);
        });
    }

    [Test]
    public void XdcSubnetBlockHeader_CreateSimulatedChild_ShouldKeepSubnetHeaderType()
    {
        XdcSubnetBlockHeader parent = Build.A.XdcSubnetBlockHeader().WithGeneratedExtraConsensusData().TestObject;
        parent.Hash = TestItem.KeccakA;
        parent.NextValidators = [1, 2, 3];

        BlockHeader child = parent.CreateSimulatedChild(parent.Timestamp + 12);

        Assert.That(child, Is.TypeOf<XdcSubnetBlockHeader>());
        XdcSubnetBlockHeader subnetChild = (XdcSubnetBlockHeader)child;
        Assert.Multiple(() =>
        {
            Assert.That(subnetChild.ParentHash, Is.EqualTo(parent.Hash));
            Assert.That(subnetChild.Number, Is.EqualTo(parent.Number + 1));
            Assert.That(subnetChild.Timestamp, Is.EqualTo(parent.Timestamp + 12));
            Assert.That(subnetChild.ExtraData, Is.Empty);
            Assert.That(subnetChild.MixHash, Is.EqualTo(Hash256.Zero));
            Assert.That(subnetChild.NextValidators, Is.Null);
        });
    }
}
