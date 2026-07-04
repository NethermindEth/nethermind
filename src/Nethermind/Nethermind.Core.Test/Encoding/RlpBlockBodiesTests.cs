// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class RlpBlockBodiesTests
{
    private sealed class TrackingMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        public bool Disposed { get; private set; }
        public Memory<byte> Memory { get; } = data;
        public void Dispose() => Disposed = true;
    }

    private static BlockBody CreateBody() =>
        Build.A.Block
            .WithTransactions(
                Build.A.Transaction.WithDataHex("0x0102030405").SignedAndResolved().TestObject,
                Build.A.Transaction.WithDataHex("0x0607080910").SignedAndResolved().TestObject)
            .WithWithdrawals(1)
            .TestObject.Body;

    private static byte[] EncodeBodyItem(BlockBody body)
    {
        byte[] data = new byte[BlockBodyDecoder.Instance.GetLength(body, RlpBehaviors.None)];
        RlpWriter writer = new(data);
        BlockBodyDecoder.Instance.Encode(ref writer, body);
        return data;
    }

    private static RlpBlockBody CreateRawBody(BlockBody body, out TrackingMemoryOwner memoryOwner)
    {
        byte[] data = EncodeBodyItem(body);
        memoryOwner = new TrackingMemoryOwner(data);
        return RlpBlockBody.FromBodyItem(memoryOwner, data);
    }

    /// <summary>Garbles the first transaction with a non-canonical length prefix so any decode throws.</summary>
    private static byte[] CorruptFirstTransaction(byte[] bodyItem)
    {
        RlpReader reader = new(bodyItem);
        reader.ReadSequenceLength(); // enter the body item
        reader.SkipLength();         // enter the txs sequence
        bodyItem[reader.Position] = 0xb8;
        bodyItem[reader.Position + 1] = 0x01;
        return bodyItem;
    }

    [Test]
    public void Lazy_decode_is_equivalent_cached_and_zero_copy()
    {
        BlockBody body = CreateBody();
        RlpBlockBodies bodies = new([CreateRawBody(body, out TrackingMemoryOwner memoryOwner), null], null);

        Assert.That(bodies.Count, Is.EqualTo(2));
        Assert.That(bodies[1], Is.Null);

        BlockBody decoded = bodies[0]!;
        Assert.That(bodies[0], Is.SameAs(decoded), "decode should be cached");
        Assert.That(EncodeBodyItem(decoded), Is.EqualTo(EncodeBodyItem(body)), "decoded body should re-encode identically");
        Assert.That(decoded.Withdrawals, Has.Length.EqualTo(1));

        memoryOwner.Memory.Span.Clear();
        Assert.That(decoded.Transactions[0].Data.ToArray(), Is.Not.EqualTo(Bytes.FromHexString("0x0102030405")),
            "transaction data should be a zero-copy slice of the backing buffer");
    }

    [Test]
    public void Should_dispose_memory_owners_and_never_decode_on_dispose()
    {
        byte[] corrupt = CorruptFirstTransaction(EncodeBodyItem(CreateBody()));

        TrackingMemoryOwner indexedOwner = new(corrupt);
        RlpBlockBodies indexed = new([RlpBlockBody.FromBodyItem(indexedOwner, corrupt)], null);
        Assert.That(() => indexed[0], Throws.InstanceOf<RlpException>(), "lazy decode should surface malformed bodies");

        TrackingMemoryOwner bodyOwner = new(corrupt);
        TrackingMemoryOwner sharedOwner = new([]);
        RlpBlockBodies disposedWithoutAccess = new([RlpBlockBody.FromBodyItem(bodyOwner, corrupt)], sharedOwner);
        disposedWithoutAccess.Dispose();
        disposedWithoutAccess.Dispose(); // Second dispose is a no-op

        Assert.That(bodyOwner.Disposed, Is.True);
        Assert.That(sharedOwner.Disposed, Is.True);
    }

    [Test]
    public void Should_copy_data_when_disowned()
    {
        BlockBody body = CreateBody();
        RlpBlockBodies bodies = new([CreateRawBody(body, out TrackingMemoryOwner memoryOwner)], null);

        bodies.Disown();
        BlockBody decoded = bodies[0]!;
        memoryOwner.Memory.Span.Clear();

        Assert.That(memoryOwner.Disposed, Is.True);
        Assert.That(decoded.Transactions[0].Data.ToArray(), Is.EqualTo(Bytes.FromHexString("0x0102030405")));
        Assert.That(decoded.Transactions[0].Hash, Is.EqualTo(body.Transactions[0].Hash));
    }
}
