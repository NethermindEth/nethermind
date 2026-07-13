// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
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
    public void GetCellsMessageSerializer_should_match_canonical_flat_rlp_vector()
    {
        GetCellsMessageSerializer72 serializer = new();
        byte[] hashBytes = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] cellMask = Convert.FromHexString("01000000000000000000000000000080");
        byte[] expected = Convert.FromHexString(
            "f401e1a0000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f9001000000000000000000000000000080");
        using GetCellsMessage72 message = new(1, [new Hash256(hashBytes)], cellMask);

        using DisposableByteBuffer serialized = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(serialized, message);
        byte[] actualBytes = new byte[serialized.ReadableBytes];
        serialized.GetBytes(serialized.ReaderIndex, actualBytes);

        using DisposableByteBuffer canonical = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        canonical.WriteBytes(expected);
        using GetCellsMessage72 deserialized = serializer.Deserialize(canonical);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualBytes, Is.EqualTo(expected), "canonical RLP");
            Assert.That(deserialized.RequestId, Is.EqualTo(1));
            Assert.That(deserialized.Hashes, Is.EqualTo(message.Hashes));
            Assert.That(deserialized.CellMask, Is.EqualTo(cellMask));
        }
    }

    [Test]
    public void CellsMessageSerializer_should_roundtrip_request_id()
    {
        CellsMessageSerializer72 serializer = new();
        byte[] cellMask = BlobCellMask.FromIndices([1]).ToBytes();
        byte[][][] cells = [[CreateCell(1)]];
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
    public void CellsMessageSerializer_should_match_canonical_flat_index_major_rlp_vector()
    {
        CellsMessageSerializer72 serializer = new();
        byte[] hashBytes = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] cellMask = Convert.FromHexString("0a000000000000000000000000000000");
        byte[][] blobMajorCells = [CreateCell(0x11), CreateCell(0x13), CreateCell(0x21), CreateCell(0x23)];
        byte[][] wireCells = [blobMajorCells[0], blobMajorCells[2], blobMajorCells[1], blobMajorCells[3]];
        byte[] expected = BuildCanonicalCellsVector(hashBytes, cellMask, wireCells);
        using CellsMessage72 message = new(1, [new Hash256(hashBytes)], [blobMajorCells], cellMask);

        using DisposableByteBuffer serialized = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(serialized, message);
        byte[] actualBytes = new byte[serialized.ReadableBytes];
        serialized.GetBytes(serialized.ReaderIndex, actualBytes);

        using DisposableByteBuffer canonical = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        canonical.WriteBytes(expected);
        using CellsMessage72 deserialized = serializer.Deserialize(canonical);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualBytes, Is.EqualTo(expected), "canonical RLP");
            Assert.That(deserialized.RequestId, Is.EqualTo(1));
            Assert.That(deserialized.Hashes, Is.EqualTo(message.Hashes));
            Assert.That(deserialized.CellMask, Is.EqualTo(cellMask));
            Assert.That(deserialized.Cells, Is.EqualTo(message.Cells), "internal blob-major layout");
        }
    }

    [TestCase(Ckzg.BytesPerCell - 1)]
    [TestCase(Ckzg.BytesPerCell + 1)]
    public void CellsMessageSerializer_should_reject_non_canonical_cell_length(int cellLength) =>
        AssertCellsMessageRejected([Hash256.Zero], [[new byte[cellLength]]], BlobCellMask.FromIndices([1]).ToBytes());

    [TestCase(1, 0)]
    [TestCase(0, 1)]
    public void CellsMessageSerializer_should_reject_mismatched_hash_and_cell_group_counts(int hashCount, int groupCount)
    {
        Hash256[] hashes = new Hash256[hashCount];
        Array.Fill(hashes, Hash256.Zero);
        byte[][][] cells = new byte[groupCount][][];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = [CreateCell(1)];
        }

        AssertCellsMessageRejected(hashes, cells, BlobCellMask.FromIndices([1]).ToBytes());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_cell_group_inconsistent_with_mask()
    {
        byte[][] cells = [CreateCell(1), CreateCell(2), CreateCell(3)];
        AssertCellsMessageRejected([Hash256.Zero], [cells], BlobCellMask.FromIndices([1, 3]).ToBytes());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_more_than_maximum_blobs_per_transaction()
    {
        byte[][] cells = new byte[Eip7594Constants.MaxBlobsPerTx + 1][];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = CreateCell((byte)i);
        }

        AssertCellsMessageRejected([Hash256.Zero], [cells], BlobCellMask.FromIndices([1]).ToBytes());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_non_empty_group_with_empty_mask() =>
        AssertCellsMessageRejected([Hash256.Zero], [[CreateCell(1)]], BlobCellMask.Empty.ToBytes());

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
        using CellsMessage72 message = new([Hash256.Zero], [[CreateCell(1)]], [1, 2]);

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
    public void GetCellsMessageSerializer_should_accept_geth_sized_request_batches()
    {
        // geth batches up to 128 hashes per GetCells request; decoding must not treat
        // requests above our own response cap as a protocol violation.
        GetCellsMessageSerializer72 serializer = new();
        Hash256[] hashes = new Hash256[2 * Eth72ProtocolHandler.MaxCellsResponseHashes];
        Array.Fill(hashes, Hash256.Zero);
        using GetCellsMessage72 message = new(hashes, BlobCellMask.FromIndices([1]).ToBytes());

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        using GetCellsMessage72 deserialized = serializer.Deserialize(buffer);
        Assert.That(deserialized.Hashes.Length, Is.EqualTo(hashes.Length));
    }

    [Test]
    public void GetCellsMessageSerializer_should_discard_hashes_beyond_local_processing_limit()
    {
        GetCellsMessageSerializer72 serializer = new();
        Hash256[] hashes = new Hash256[Eth72ProtocolHandler.MaxCellsRequestHashes + 1];
        Array.Fill(hashes, Hash256.Zero);

        using GetCellsMessage72 message = new(hashes, BlobCellMask.FromIndices([1]).ToBytes());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        using GetCellsMessage72 deserialized = serializer.Deserialize(buffer);
        Assert.That(deserialized.Hashes.Length, Is.EqualTo(Eth72ProtocolHandler.MaxCellsRequestHashes));
    }

    [Test]
    public void GetCellsMessageSerializer_should_reject_null_transaction_hash()
    {
        GetCellsMessageSerializer72 serializer = new();
        using GetCellsMessage72 message = new([null!], BlobCellMask.FromIndices([1]).ToBytes());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_null_transaction_hash()
    {
        CellsMessageSerializer72 serializer = new();
        using CellsMessage72 message = new([null!], [[CreateCell(1)]], BlobCellMask.FromIndices([1]).ToBytes());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_null_transaction_hash()
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();
        using NewPooledTransactionHashesMessage72 message = new(
            [(byte)TxType.Blob],
            [1],
            [null!],
            BlobCellMask.Full.ToBytes());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [TestCase(0)]
    [TestCase(2)]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_invalid_cell_mask_length(int maskLength)
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();
        using NewPooledTransactionHashesMessage72 message = new([1], [1], [Hash256.Zero], new byte[maskLength]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [TestCase(1, 0, 1)]
    [TestCase(1, 1, 0)]
    [TestCase(0, 1, 1)]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_mismatched_field_counts(
        int typeCount,
        int sizeCount,
        int hashCount)
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();
        byte[] types = new byte[typeCount];
        Array.Fill<byte>(types, 1);
        int[] sizes = new int[sizeCount];
        Array.Fill(sizes, 1);
        Hash256[] hashes = new Hash256[hashCount];
        Array.Fill(hashes, Hash256.Zero);
        using NewPooledTransactionHashesMessage72 message = new(types, sizes, hashes, BlobCellMask.Empty.ToBytes());

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

    private static byte[] CreateCell(byte value)
    {
        byte[] cell = new byte[Ckzg.BytesPerCell];
        Array.Fill(cell, value);
        return cell;
    }

    private static void AssertCellsMessageRejected(Hash256[] hashes, byte[][][] cells, byte[] cellMask)
    {
        CellsMessageSerializer72 serializer = new();
        using CellsMessage72 message = new(hashes, cells, cellMask);
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.InstanceOf<RlpException>());
    }

    private static byte[] BuildCanonicalCellsVector(byte[] hash, byte[] cellMask, byte[][] wireCells)
    {
        // Exact RLP prefixes for [1, [B32], [[4 * B2048]], B16].
        byte[] vector = new byte[8265];
        int offset = 0;
        Write(vector, ref offset, [0xf9, 0x20, 0x46, 0x01, 0xe1, 0xa0]);
        Write(vector, ref offset, hash);
        Write(vector, ref offset, [0xf9, 0x20, 0x0f, 0xf9, 0x20, 0x0c]);
        for (int i = 0; i < wireCells.Length; i++)
        {
            Write(vector, ref offset, [0xb9, 0x08, 0x00]);
            Write(vector, ref offset, wireCells[i]);
        }

        vector[offset++] = 0x90;
        Write(vector, ref offset, cellMask);
        Assert.That(offset, Is.EqualTo(vector.Length), "vector length");
        return vector;
    }

    private static void Write(byte[] destination, ref int offset, byte[] source)
    {
        source.CopyTo(destination, offset);
        offset += source.Length;
    }
}
