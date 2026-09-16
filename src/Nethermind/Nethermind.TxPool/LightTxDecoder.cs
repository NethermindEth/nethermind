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
    // Format 2 stored the already-derived elided network-encoding size. It remains readable, while new records keep
    // the foundational consensus size from which future wrapper encodings can also be derived.
    private const byte ElidedNetworkEncodingSizeFormatVersion = 2;

    private static int GetLength(Transaction tx, int networkSize, int persistedEncodingSize, byte sizeFormatVersion) => Rlp.LengthOf(tx.Timestamp)
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
               + Rlp.LengthOf(networkSize)
               + Rlp.LengthOf(sizeof(byte))
               + Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0)
               + Rlp.LengthOf(persistedEncodingSize)
               + Rlp.LengthOf(sizeFormatVersion);

    public static byte[] Encode(Transaction tx)
    {
        int networkSize = tx.GetLength();
        (int persistedEncodingSize, byte sizeFormatVersion) = GetPersistedEncodingSize(tx);
        byte[] bytes = new byte[GetLength(tx, networkSize, persistedEncodingSize, sizeFormatVersion)];
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
        writer.Encode(networkSize);
        writer.Encode((byte)(tx.GetProofVersion() ?? default));
        EncodeAvailableCellMask(tx, ref writer);
        writer.Encode(persistedEncodingSize);
        writer.Encode(sizeFormatVersion);

        return bytes;
    }

    public static LightTransaction Decode(byte[] data)
    {
        RlpReader ctx = new(data);
        UInt256 timestamp = ctx.DecodeUInt256();
        Address sender = ctx.DecodeAddress();
        ulong nonce = ctx.DecodeULong();
        Hash256 hash = ctx.DecodeKeccak();
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
        int consensusEncodingSize = sizeFormatVersion == ConsensusEncodingSizeFormatVersion ? persistedEncodingSize : 0;
        int elidedNetworkEncodingSize = sizeFormatVersion == ElidedNetworkEncodingSizeFormatVersion ? persistedEncodingSize : 0;
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
            consensusEncodingSize,
            elidedNetworkEncodingSize);
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
            : tx is LightTransaction lightTx
                ? lightTx.BlobCellMask
                : BlobCellMask.Empty;

    private static (int Size, byte FormatVersion) GetPersistedEncodingSize(Transaction tx)
    {
        if (tx is not LightTransaction lightTx)
        {
            return (tx.GetLength(shouldCountBlobs: false), ConsensusEncodingSizeFormatVersion);
        }

        int consensusEncodingSize = lightTx.GetConsensusEncodingSize();
        return consensusEncodingSize > 0
            ? (consensusEncodingSize, ConsensusEncodingSizeFormatVersion)
            : (lightTx.GetElidedNetworkEncodingSize(), ElidedNetworkEncodingSizeFormatVersion);
    }
}
