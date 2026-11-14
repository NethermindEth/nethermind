// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class OwnedBlockBodiesTests
{
    [Test]
    public void Transaction_ownership_follows_disowning([Values] bool ownsPooledTransactions, [Values] bool disown)
    {
        Transaction transaction = Build.A.Transaction.Signed().TestObject;
        using OwnedBlockBodies bodies = new([new BlockBody([transaction], [])], ownsPooledTransactions);

        if (disown) bodies.Disown();
        bodies.Dispose();
        bool returned = ownsPooledTransactions && !disown;
        Assert.That(transaction.Signature is null, Is.EqualTo(returned));
    }

    [Test]
    public void Encoded_bodies_decode_when_read([Values] bool missingBody)
    {
        BlockBody body = CreateBody(2);
        using OwnedBlockBodies bodies = CreateEncoded(missingBody ? null : body, out IMemoryOwner<byte> _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bodies.Count, Is.EqualTo(1));
            Assert.That(bodies.GetEncoded(0).ToArray(), Is.EqualTo(Encode(missingBody ? null : body)));
            if (missingBody) Assert.That(bodies.Bodies[0], Is.Null);
            else Assert.That(bodies.Bodies[0], Is.EqualTo(body).UsingBlockBodyComparer());
        }
    }

    [Test]
    public void Decoded_bodies_encode_when_read([Values] bool missingBody)
    {
        BlockBody body = CreateBody(2);
        using OwnedBlockBodies bodies = new([missingBody ? null : body]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bodies.GetEncoded(0).ToArray(), Is.EqualTo(Encode(missingBody ? null : body)));
            Assert.That(bodies.GetEncoded(0).Equals(bodies.GetEncoded(0)), Is.True, "encoded once");
        }
    }

    [Test]
    public void Disowned_bodies_outlive_their_memory([Values] bool decodedBeforeDisown)
    {
        BlockBody body = CreateBody(2);
        OwnedBlockBodies bodies = CreateEncoded(body, out IMemoryOwner<byte> memory);
        if (decodedBeforeDisown)
        {
            _ = bodies.Bodies;
            memory.Received(1).Dispose();
        }

        bodies.Disown();
        memory.Received(1).Dispose();
        if (decodedBeforeDisown)
        {
            Assert.That(bodies.Bodies[0], Is.EqualTo(body).UsingBlockBodyComparer());
        }
        else
        {
            Assert.Throws<ObjectDisposedException>(() => _ = bodies.Bodies);
            Assert.Throws<ObjectDisposedException>(() => bodies.GetEncoded(0));
        }

        bodies.Dispose();
        memory.Received(1).Dispose();
    }

    [Test]
    public void Dispose_returns_decoded_transactions_and_memory()
    {
        BlockBody body = CreateBody(1);
        OwnedBlockBodies bodies = CreateEncoded(body, out IMemoryOwner<byte> memory);
        Transaction decoded = bodies.Bodies[0]!.Transactions[0];

        bodies.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Signature, Is.Null);
            memory.Received(1).Dispose();
        }
    }

    [Test]
    public void Bodies_of_encoded_blocks_are_taken_as_they_are()
    {
        Block[] blocks =
        [
            Build.A.Block.WithTransactions(Build.A.Transaction.Signed().TestObjectNTimes(3)).TestObject,
            Build.A.Block.WithUncles(Build.A.Block.TestObject).WithWithdrawals(2).TestObject,
        ];
        BlockDecoder decoder = new();

        using OwnedBlockBodies bodies = OwnedBlockBodies.FromEncodedBlocks([decoder.Encode(blocks[0]), decoder.Encode(blocks[1])]);

        Assert.That(bodies.Count, Is.EqualTo(blocks.Length));
        for (int i = 0; i < blocks.Length; i++)
        {
            Assert.That(bodies.GetEncoded(i).ToArray(), Is.EqualTo(Encode(blocks[i].Body)));
        }
    }

    private static BlockBody CreateBody(int transactionCount) =>
        new(Build.A.Transaction.Signed().TestObjectNTimes(transactionCount), []);

    private static OwnedBlockBodies CreateEncoded(BlockBody? body, out IMemoryOwner<byte> memory)
    {
        byte[] encoded = Encode(body);
        memory = Substitute.For<IMemoryOwner<byte>>();
        memory.Memory.Returns(encoded.AsMemory());
        return new OwnedBlockBodies(memory, [new Range(0, encoded.Length)], BlockBodyDecoder.Instance);
    }

    private static byte[] Encode(BlockBody? body) =>
        body is null ? [Rlp.EmptyListByte] : BlockBodyDecoder.Instance.Encode(body);
}
