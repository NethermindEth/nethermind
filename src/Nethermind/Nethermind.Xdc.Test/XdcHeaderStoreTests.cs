// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    public void XdcHeaderStore_ShouldInheritFromHeaderStore()
    {
        Assert.That(_headerStore, Is.InstanceOf<HeaderStore>());
    }

    [Test]
    public void XdcHeaderStore_InsertAndGetHeader_ShouldWorkCorrectly()
    {
        // Arrange
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData();
        var header = headerBuilder.TestObject;

        // Act
        _headerStore.Insert(header);
        XdcBlockHeader? retrievedHeader = _headerStore.Get(header.Hash!, false);
        // Assert
        Assert.That(retrievedHeader, Is.Not.Null);
        Assert.That(retrievedHeader!.Hash, Is.EqualTo(header.Hash));
        Assert.That(retrievedHeader.Number, Is.EqualTo(header.Number));
        Assert.That(retrievedHeader, Is.InstanceOf<XdcBlockHeader>());
    }

    [Test]
    public void XdcBlockStore_InsertAndGetBlock_ShouldWorkCorrectly()
    {
        // Arrange
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData();
        var header = headerBuilder.TestObject;
        BlockBuilder blockBuilder = Build.A.Block.WithHeader(header);
        var block = blockBuilder.TestObject;
        // Act
        _blockStore.Insert(block);
        Block? retrievedBlock = _blockStore.Get(block.Number, block.Hash!);
        // Assert
        Assert.That(retrievedBlock, Is.Not.Null);
        Assert.That(retrievedBlock!.Hash, Is.EqualTo(block.Hash));
        Assert.That(retrievedBlock.Header.Hash, Is.EqualTo(block.Header.Hash));
    }
}
