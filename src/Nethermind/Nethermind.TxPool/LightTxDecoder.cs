// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.TxPool;

public class LightTxDecoder : TxDecoder<Transaction>
{
    private const byte ConsensusEncodingSizeFormatVersion = 1;

    private static int GetLength(Transaction tx) => Rlp.LengthOf(tx.Timestamp)
               + Rlp.LengthOf(tx.SenderAddress)
               + Rlp.LengthOf(tx.Nonce)
               + Rlp.LengthOf(tx.Hash)
               + Rlp.LengthOf(tx.Value)
               + Rlp.LengthOf(tx.GasLimit)
               + Rlp.LengthOf(tx.GasPrice)
               + Rlp.LengthOf(tx.DecodedMaxFeePerGas)
               + Rlp.LengthOf(tx.MaxFeePerBlobGas!.Value)
               + Rlp.LengthOf(tx.BlobVersionedHashes!)
               + Rlp.LengthOf(tx.PoolIndex)
               + Rlp.LengthOf(tx.GetLength())
               + Rlp.LengthOf(sizeof(byte))
               + Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0)
               + Rlp.LengthOf(GetConsensusEncodingSize(tx))
               + Rlp.LengthOf(ConsensusEncodingSizeFormatVersion);

    public static byte[] Encode(Transaction tx)
    {
        byte[] bytes = new byte[GetLength(tx)];
        RlpWriter writer = new(bytes);

        writer.Encode(tx.Timestamp);
        writer.Encode(tx.SenderAddress);
        writer.Encode(tx.Nonce);
        writer.Encode(tx.Hash);
        writer.Encode(in tx.ValueRef);
        writer.Encode(tx.GasLimit);
        writer.Encode(tx.GasPrice);
        writer.Encode(tx.DecodedMaxFeePerGas);
        writer.Encode(tx.MaxFeePerBlobGas!.Value);
        writer.Encode(tx.BlobVersionedHashes!);
        writer.Encode(tx.PoolIndex);
        writer.Encode(tx.GetLength());
        writer.Encode((byte)((tx.NetworkWrapper as ShardBlobNetworkWrapper)?.Version ?? default));
        EncodeAvailableCellMask(tx, ref writer);
        writer.Encode(GetConsensusEncodingSize(tx));
        writer.Encode(ConsensusEncodingSizeFormatVersion);

        return bytes;
    }

    public static LightTransaction Decode(byte[] data)
    {
        RlpReader ctx = new(data);
        UInt256 timestamp = ctx.DecodeUInt256();
        Address sender = ctx.DecodeAddress()!;
        ulong nonce = ctx.DecodeULong();
        Hash256 hash = ctx.DecodeKeccak()!;
        UInt256 value = ctx.DecodeUInt256();
        ulong gasLimit = ctx.DecodeULong();
        UInt256 gasPrice = ctx.DecodeUInt256();
        UInt256 maxFeePerGas = ctx.DecodeUInt256();
        UInt256 maxFeePerBlobGas = ctx.DecodeUInt256();
        byte[][] blobVersionHashes = ctx.DecodeByteArrays(BlobTxDecoder<Transaction>.BlobVersionedHashesCountLimit, innerSize: Hash256.Size);
        ulong poolIndex = ctx.DecodeULong();
        int size = ctx.DecodePositiveInt();

        int optionalFieldCount = ctx.PeekNumberOfItemsRemaining(maxSearch: 5);
        if (optionalFieldCount > 4)
        {
            throw new RlpException($"Too many optional fields in {nameof(LightTransaction)}.");
        }

        ProofVersion proofVersion = optionalFieldCount >= 1 ? (ProofVersion)ctx.DecodeByte() : default;
        // Entries persisted before the mask field was added always hold full blobs.
        BlobCellMask blobCellMask = optionalFieldCount >= 2
            ? BlobCellMask.FromBytes(ctx.DecodeByteArraySpan())
            : BlobCellMask.Full;
        int persistedEncodingSize = optionalFieldCount >= 3 ? ctx.DecodePositiveInt() : 0;
        byte sizeFormatVersion = optionalFieldCount >= 4 ? (byte)ctx.DecodeByte() : (byte)0;
        int consensusEncodingSize = sizeFormatVersion == ConsensusEncodingSizeFormatVersion
            ? persistedEncodingSize
            : 0;
        ctx.Check(data.Length);

        return new LightTransaction(
            timestamp,
            sender,
            nonce,
            hash,
            value,
            gasLimit,
            gasPrice,
            maxFeePerGas,
            maxFeePerBlobGas,
            blobVersionHashes,
            poolIndex,
            size,
            proofVersion,
            blobCellMask,
            consensusEncodingSize);
    }

    private static void EncodeAvailableCellMask(Transaction tx, ref RlpWriter writer)
    {
        Span<byte> bytes = stackalloc byte[BlobCellMask.FixedByteLength];
        GetAvailableCellMask(tx).WriteTo(bytes);
        writer.Encode(bytes);
    }

    private static BlobCellMask GetAvailableCellMask(Transaction tx) =>
        tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            ? wrapper.GetAvailableCellMask()
            : BlobCellMask.Empty;

    private static int GetConsensusEncodingSize(Transaction tx) =>
        tx is LightTransaction lightTx && lightTx.GetConsensusEncodingSize() > 0
            ? lightTx.GetConsensusEncodingSize()
            : tx.GetLength(shouldCountBlobs: false);
}
