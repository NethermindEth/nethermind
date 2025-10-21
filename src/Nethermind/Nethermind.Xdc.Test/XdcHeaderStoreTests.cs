// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Headers;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcHeaderStoreTests
{
    IXdcHeaderStore _headerStore;
    IDb headerDb;
    IDb blockNumDb;

    [SetUp]
    public void Setup()
    {
        headerDb = new MemDb();
        blockNumDb = new MemDb();

        _headerStore = new XdcHeaderStore(headerDb, blockNumDb);
    }

    [Test]
    public void XdcHeaderStore_ShouldInheritFromHeaderStore()
    {
        Assert.That(_headerStore, Is.InstanceOf<HeaderStore>());
    }

    [Test]
    public void XdcHeaderStore_ShouldUseXdcHeaderDecoder()
    {
        var xdcHeaderStore = _headerStore as XdcHeaderStore;
        Assert.That(xdcHeaderStore, Is.Not.Null);
        var headerDecoderField = typeof(HeaderStore).GetProperty("_headerDecoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var headerDecoder = headerDecoderField!.GetValue(xdcHeaderStore);
        Assert.That(headerDecoder, Is.InstanceOf<XdcHeaderDecoder>());
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


}
