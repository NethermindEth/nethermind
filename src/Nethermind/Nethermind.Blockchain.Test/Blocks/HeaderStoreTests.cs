// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

public class HeaderStoreTests
{

    [Test]
    public void TestCanStoreAndGetHeader()
    {
        HeaderStore store = new(new MemDb(), new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        BlockHeader header2 = Build.A.BlockHeader.WithNumber(102).TestObject;

        store.Get(header.Hash!).Should().BeNull();
        store.Get(header2.Hash!).Should().BeNull();

        store.Store(header);
        store.Get(header.Hash!)!.Hash.Should().Be(header.Hash!);
        store.Get(header2.Hash!).Should().BeNull();

        store.Store(header2);
        store.Get(header.Hash!)!.Hash.Should().Be(header.Hash!);
        store.Get(header2.Hash!)!.Hash.Should().Be(header2.Hash!);
    }

    [Test]
    public void TestCanReadHeaderStoredWithHash()
    {
        IDb headerDb = new MemDb();
        HeaderStore store = new(headerDb, new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        headerDb.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        store.Get(header.Hash!)!.Hash.Should().Be(header.Hash!);
    }

    [Test]
    public void TestCanReadCacheHeader()
    {
        HeaderStore store = new(new MemDb(), new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Cache(header);
        store.Get(header.Hash!)!.Hash.Should().Be(header.Hash!);
    }

    [Test]
    public void TestCanDeleteHeader()
    {
        HeaderStore store = new(new MemDb(), new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Store(header);
        store.Delete(header.Hash!);

        store.Get(header.Hash!).Should().BeNull();
    }

    [Test]
    public void TestCanDeleteHeaderStoredWithHash()
    {
        IDb headerDb = new MemDb();
        HeaderStore store = new(headerDb, new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        headerDb.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        store.Delete(header.Hash!);
        store.Get(header.Hash!)!.Should().BeNull();
    }

}
