// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Merge.Plugin.Data;

internal static class GetPayloadDirectResponseWriter
{
    public static ValueTask WriteV5Async(
        PipeWriter writer,
        Block block,
        UInt256 blockValue,
        BlobsBundleV2 blobsBundle,
        byte[][]? executionRequests,
        bool shouldOverrideBuilder,
        CancellationToken cancellationToken) =>
        WriteAsync(writer, block, blockValue, blobsBundle, executionRequests, shouldOverrideBuilder, includeV6Fields: false, cancellationToken);

    public static ValueTask WriteV6Async(
        PipeWriter writer,
        Block block,
        UInt256 blockValue,
        BlobsBundleV2 blobsBundle,
        byte[][]? executionRequests,
        bool shouldOverrideBuilder,
        CancellationToken cancellationToken) =>
        WriteAsync(writer, block, blockValue, blobsBundle, executionRequests, shouldOverrideBuilder, includeV6Fields: true, cancellationToken);

    private static async ValueTask WriteAsync(
        PipeWriter writer,
        Block block,
        UInt256 blockValue,
        BlobsBundleV2 blobsBundle,
        byte[][]? executionRequests,
        bool shouldOverrideBuilder,
        bool includeV6Fields,
        CancellationToken cancellationToken)
    {
        writer.Write("{\"blockValue\":"u8);
        HexWriter.WriteUInt256HexString(writer, blockValue);
        writer.Write(",\"executionPayload\":"u8);
        if (await WriteExecutionPayloadAsync(writer, block, includeV6Fields, cancellationToken))
        {
            return;
        }

        writer.Write(",\"blobsBundle\":"u8);
        if (await WriteBlobsBundleAsync(writer, blobsBundle, cancellationToken))
        {
            return;
        }

        writer.Write(",\"shouldOverrideBuilder\":"u8);
        writer.Write(shouldOverrideBuilder ? "true"u8 : "false"u8);

        if (executionRequests is not null)
        {
            writer.Write(",\"executionRequests\":"u8);
            if (await WriteHexByteArraysAsync(writer, executionRequests, chunked: false, cancellationToken))
            {
                return;
            }
        }

        writer.Write("}"u8);
    }

    private static async ValueTask<bool> WriteExecutionPayloadAsync(
        PipeWriter writer,
        Block block,
        bool includeV6Fields,
        CancellationToken cancellationToken)
    {
        writer.Write("{\"parentHash\":"u8);
        WriteHexString(writer, block.ParentHash!.Bytes, chunked: false);
        writer.Write(",\"feeRecipient\":"u8);
        WriteHexString(writer, block.Beneficiary!.Bytes, chunked: false);
        writer.Write(",\"stateRoot\":"u8);
        WriteHexString(writer, block.StateRoot!.Bytes, chunked: false);
        writer.Write(",\"receiptsRoot\":"u8);
        WriteHexString(writer, block.ReceiptsRoot!.Bytes, chunked: false);
        writer.Write(",\"logsBloom\":"u8);
        WriteHexString(writer, block.Bloom!.Bytes, chunked: false);
        writer.Write(",\"prevRandao\":"u8);
        WriteHexString(writer, (block.MixHash ?? Keccak.Zero).Bytes, chunked: false);
        writer.Write(",\"blockNumber\":"u8);
        WriteLongHexString(writer, block.Number);
        writer.Write(",\"gasLimit\":"u8);
        WriteLongHexString(writer, block.GasLimit);
        writer.Write(",\"gasUsed\":"u8);
        WriteLongHexString(writer, block.GasUsed);
        writer.Write(",\"timestamp\":"u8);
        HexWriter.WriteUlongHexString(writer, block.Timestamp);
        writer.Write(",\"extraData\":"u8);
        WriteHexString(writer, block.ExtraData!, chunked: false);
        writer.Write(",\"baseFeePerGas\":"u8);
        HexWriter.WriteUInt256HexString(writer, block.BaseFeePerGas);
        writer.Write(",\"blockHash\":"u8);
        WriteHexString(writer, block.Hash!.Bytes, chunked: false);
        writer.Write(",\"transactions\":"u8);

        if (await WriteTransactionsAsync(writer, block, cancellationToken))
        {
            return true;
        }

        if (block.Withdrawals is not null)
        {
            writer.Write(",\"withdrawals\":"u8);
            PayloadBodiesDirectResponseWriter.WriteWithdrawalArray(writer, block.Withdrawals);
        }

        writer.Write(",\"blobGasUsed\":"u8);
        WriteNullableUlongHexString(writer, block.BlobGasUsed);
        writer.Write(",\"excessBlobGas\":"u8);
        WriteNullableUlongHexString(writer, block.ExcessBlobGas);

        if (includeV6Fields)
        {
            writer.Write(",\"blockAccessList\":"u8);
            WriteBlockAccessList(writer, block);
            writer.Write(",\"slotNumber\":"u8);
            WriteNullableUlongHexString(writer, block.SlotNumber);
        }

        writer.Write("}"u8);
        return false;
    }

    private static async ValueTask<bool> WriteTransactionsAsync(PipeWriter writer, Block block, CancellationToken cancellationToken)
    {
        if (block.EncodedTransactions is { } encodedTransactions)
        {
            return await WriteHexByteArraysAsync(writer, encodedTransactions, chunked: true, cancellationToken);
        }

        Transaction[] transactions = block.Transactions;
        writer.Write("["u8);

        for (int i = 0, count = transactions.Length; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);
            PayloadBodiesDirectResponseWriter.WriteTransaction(writer, transactions[i]);

            if (await StreamableResultWriter.FlushIfNeededAsync(writer, cancellationToken))
            {
                return true;
            }
        }

        writer.Write("]"u8);
        return false;
    }

    private static void WriteBlockAccessList(IBufferWriter<byte> writer, Block block)
    {
        if (block.EncodedBlockAccessList is { } encodedBlockAccessList)
        {
            HexWriter.WriteHexString(writer, encodedBlockAccessList, chunked: encodedBlockAccessList.Length > PayloadBodiesDirectResponseWriter.HexChunkThreshold);
            return;
        }

        if (block.BlockAccessList is not { } blockAccessList)
        {
            writer.Write("null"u8);
            return;
        }

        WriteBlockAccessList(writer, blockAccessList);
    }

    private static void WriteBlockAccessList(IBufferWriter<byte> writer, ReadOnlyBlockAccessList blockAccessList)
    {
        BlockAccessListDecoder decoder = BlockAccessListDecoder.Instance;
        int length = decoder.GetLength(blockAccessList, RlpBehaviors.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);

        RlpWriter rlpWriter = new(buffer);
        decoder.Encode(ref rlpWriter, blockAccessList);
        HexWriter.WriteHexString(writer, buffer.AsSpan(0, length), chunked: length > PayloadBodiesDirectResponseWriter.HexChunkThreshold);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private static async ValueTask<bool> WriteBlobsBundleAsync(
        PipeWriter writer,
        BlobsBundleV2 blobsBundle,
        CancellationToken cancellationToken)
    {
        writer.Write("{\"commitments\":"u8);
        if (await WriteHexByteArraysAsync(writer, blobsBundle.Commitments, chunked: false, cancellationToken))
        {
            return true;
        }

        writer.Write(",\"blobs\":"u8);
        if (await WriteHexByteArraysAsync(writer, blobsBundle.Blobs, chunked: true, cancellationToken))
        {
            return true;
        }

        writer.Write(",\"proofs\":"u8);
        if (await WriteHexByteArraysAsync(writer, blobsBundle.Proofs, chunked: false, cancellationToken))
        {
            return true;
        }

        writer.Write("}"u8);
        return false;
    }

    private static async ValueTask<bool> WriteHexByteArraysAsync(
        PipeWriter writer,
        byte[][] items,
        bool chunked,
        CancellationToken cancellationToken)
    {
        writer.Write("["u8);

        for (int i = 0, count = items.Length; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);
            HexWriter.WriteHexString(writer, items[i], chunked);

            if (await StreamableResultWriter.FlushIfNeededAsync(writer, cancellationToken))
            {
                return true;
            }
        }

        writer.Write("]"u8);
        return false;
    }

    private static void WriteNullableUlongHexString(IBufferWriter<byte> writer, ulong? value)
    {
        if (value is null)
        {
            writer.Write("null"u8);
            return;
        }

        HexWriter.WriteUlongHexString(writer, value.GetValueOrDefault());
    }

    private static void WriteLongHexString(IBufferWriter<byte> writer, long value) =>
        HexWriter.WriteUlongHexString(writer, (ulong)value);

    private static void WriteHexString(IBufferWriter<byte> writer, ReadOnlySpan<byte> data, bool chunked) =>
        HexWriter.WriteHexString(writer, data, chunked);
}
