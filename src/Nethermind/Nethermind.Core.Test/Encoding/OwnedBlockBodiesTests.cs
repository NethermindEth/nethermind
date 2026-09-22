// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class OwnedBlockBodiesTests
{
    [Test]
    public void Transaction_ownership_is_independent_of_memory_ownership(
        [Values] bool hasMemoryOwner, [Values] bool ownsPooledTransactions, [Values] bool disown)
    {
        IMemoryOwner<byte>? memoryOwner = hasMemoryOwner ? Substitute.For<IMemoryOwner<byte>>() : null;
        Transaction transaction = Build.A.Transaction.Signed().TestObject;
        using OwnedBlockBodies bodies = new([new BlockBody([transaction], [])], memoryOwner, ownsPooledTransactions);

        if (disown) bodies.Disown();
        bodies.Dispose();
        bool returned = ownsPooledTransactions && !disown;
        Assert.That(transaction.Signature is null, Is.EqualTo(returned));

        bodies.Dispose();
        memoryOwner?.Received(1).Dispose();
    }

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
        Assert.That(blockBodies[0].Transactions[0].Data.ToArray(), Is.EqualTo(actualMemoryOwner.Memory.ToArray()));

        OwnedBlockBodies ownedBlockBodies = new(blockBodies, memoryOwner);
        ownedBlockBodies.Disown();
        actualMemoryOwner.Memory.Span.Clear();
        Assert.That(blockBodies[0].Transactions[0].Data.ToArray(), Is.Not.EqualTo(actualMemoryOwner.Memory.ToArray()));
    }
}
