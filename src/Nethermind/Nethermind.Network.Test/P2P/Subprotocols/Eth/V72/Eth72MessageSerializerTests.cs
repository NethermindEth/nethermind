// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V72;

[TestFixture, Parallelizable(ParallelScope.All)]
public class Eth72MessageSerializerTests
{
    [Test]
    public void BlobCellMask_should_use_little_endian_wire_bit_order()
    {
        BlobCellMask mask = BlobCellMask.FromIndices([0, 1, 7, 8, 127]);
        byte[] expected =
        [
            0b1000_0011,
            0b0000_0001,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0b1000_0000,
        ];

        Assert.That(mask.ToBytes(), Is.EqualTo(expected));
        Assert.That(BlobCellMask.FromBytes(expected), Is.EqualTo(mask));
    }

    [Test]
    public void GetCellsMessageSerializer_should_roundtrip_request_id()
    {
        GetCellsMessageSerializer72 serializer = new();
        byte[] cellMask = BlobCellMask.FromIndices([1, 7]).ToBytes();
        using GetCellsMessage72 message = new(1234, [Hash256.Zero], cellMask);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);
        using GetCellsMessage72 actual = serializer.Deserialize(buffer);

        Assert.That(actual.RequestId, Is.EqualTo(message.RequestId));
        Assert.That(actual.Hashes, Is.EqualTo(message.Hashes));
        Assert.That(actual.CellMask, Is.EqualTo(cellMask));
    }

    [Test]
    public void CellsMessageSerializer_should_roundtrip_request_id()
    {
        CellsMessageSerializer72 serializer = new();
        byte[] cellMask = BlobCellMask.FromIndices([1]).ToBytes();
        byte[][][] cells = [[[1, 2, 3]]];
        using CellsMessage72 message = new(5678, [Hash256.Zero], cells, cellMask);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);
        using CellsMessage72 actual = serializer.Deserialize(buffer);

        Assert.That(actual.RequestId, Is.EqualTo(message.RequestId));
        Assert.That(actual.Hashes, Is.EqualTo(message.Hashes));
        Assert.That(actual.CellMask, Is.EqualTo(cellMask));
        Assert.That(actual.Cells[0][0], Is.EqualTo(cells[0][0]));
    }

    [Test]
    public void GetCellsMessageSerializer_should_reject_invalid_cell_mask_length()
    {
        GetCellsMessageSerializer72 serializer = new();
        using GetCellsMessage72 message = new([Hash256.Zero], [1, 2]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_invalid_cell_mask_length()
    {
        CellsMessageSerializer72 serializer = new();
        using CellsMessage72 message = new([Hash256.Zero], [[[]]], [1, 2]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_oversized_cell_groups_before_accepting_message()
    {
        CellsMessageSerializer72 serializer = new();
        byte[][] cells = new byte[BlobCellMask.CellCount * Eip7594Constants.MaxBlobsPerTx + 1][];
        Array.Fill(cells, []);
        using CellsMessage72 message = new([Hash256.Zero], [cells], BlobCellMask.Full.ToBytes());

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_more_than_response_hash_limit()
    {
        CellsMessageSerializer72 serializer = new();
        Hash256[] hashes = new Hash256[Eth72ProtocolHandler.MaxCellsResponseHashes + 1];
        Array.Fill(hashes, Hash256.Zero);
        byte[][][] cells = new byte[hashes.Length][][];
        Array.Fill(cells, [[]]);
        using CellsMessage72 message = new(hashes, cells, BlobCellMask.FromIndices([1]).ToBytes());

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void GetCellsMessageSerializer_should_reject_more_than_response_hash_limit()
    {
        GetCellsMessageSerializer72 serializer = new();
        Hash256[] hashes = new Hash256[Eth72ProtocolHandler.MaxCellsResponseHashes + 1];
        Array.Fill(hashes, Hash256.Zero);
        using GetCellsMessage72 message = new(hashes, BlobCellMask.FromIndices([1]).ToBytes());

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_invalid_non_empty_cell_mask_length()
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();
        using NewPooledTransactionHashesMessage72 message = new([1], [1], [Hash256.Zero], [1, 2]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_oversized_cell_mask_before_copying()
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();
        byte[] cellMask = new byte[BlobCellMask.FixedByteLength + 1];
        using NewPooledTransactionHashesMessage72 message = new([1], [1], [Hash256.Zero], cellMask);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpLimitException>());
    }

    [TestCase(0UL)]
    [TestCase(2_147_483_648UL)]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_non_positive_or_overflowed_sizes(ulong size)
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        ByteBufferRlpWriter writer = new(buffer);
        byte[] types = [1];
        int sizesLength = Rlp.LengthOf(size);
        int hashesLength = Rlp.LengthOf(Hash256.Zero);
        int totalSize = Rlp.LengthOf(types)
            + Rlp.LengthOfSequence(sizesLength)
            + Rlp.LengthOfSequence(hashesLength)
            + Rlp.LengthOf(BlobCellMask.Full.ToBytes());
        writer.StartSequence(totalSize);
        writer.Encode(types);
        writer.StartSequence(sizesLength);
        writer.Encode(size);
        writer.StartSequence(hashesLength);
        writer.Encode(Hash256.Zero);
        writer.Encode(BlobCellMask.Full.ToBytes());

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_missing_cell_mask()
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        ByteBufferRlpWriter writer = new(buffer);
        byte[] types = [1];
        int sizesLength = Rlp.LengthOf(1);
        int hashesLength = Rlp.LengthOf(Hash256.Zero);
        int totalSize = Rlp.LengthOf(types)
            + Rlp.LengthOfSequence(sizesLength)
            + Rlp.LengthOfSequence(hashesLength);
        writer.StartSequence(totalSize);
        writer.Encode(types);
        writer.StartSequence(sizesLength);
        writer.Encode(1);
        writer.StartSequence(hashesLength);
        writer.Encode(Hash256.Zero);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }
}
