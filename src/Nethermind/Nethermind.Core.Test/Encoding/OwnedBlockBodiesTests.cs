// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class OwnedBlockBodiesTests
{
    [Test]
    public void Should_dispose_memory_owner()
    {
        IMemoryOwner<byte> memoryOwner = Substitute.For<IMemoryOwner<byte>>();
        BlockBody[] blockBodies = { Build.A.Block.WithTransactions(2, MainnetSpecProvider.Instance).TestObject.Body };
        OwnedBlockBodies ownedBlockBodies = new(blockBodies, memoryOwner);
        ownedBlockBodies.Dispose();
        memoryOwner.Received().Dispose();
    }

    [Test]
    public void Should_copy_data_when_disowned()
    {
        IMemoryOwner<byte> actualMemoryOwner = MemoryPool<byte>.Shared.Rent(100);
        IMemoryOwner<byte> memoryOwner = Substitute.For<IMemoryOwner<byte>>();
        BlockBody[] blockBodies = { Build.A.Block.WithTransactions(1, MainnetSpecProvider.Instance).TestObject.Body };
        blockBodies[0].Transactions[0].Data = actualMemoryOwner.Memory;
        actualMemoryOwner.Memory.Span.Fill(1);
        blockBodies[0].Transactions[0].Data.Should().BeEquivalentTo(actualMemoryOwner.Memory);

        OwnedBlockBodies ownedBlockBodies = new(blockBodies, memoryOwner);
        ownedBlockBodies.Disown();
        actualMemoryOwner.Memory.Span.Fill(0);
        blockBodies[0].Transactions[0].Data.Should().NotBeEquivalentTo(actualMemoryOwner.Memory);
    }
}
