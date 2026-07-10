// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.TxPool;

public class LightTxDecoder : TxDecoder<Transaction>
{
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
               + Rlp.LengthOf(sizeof(byte));

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

        return bytes;
    }

    public static LightTransaction Decode(byte[] data)
    {
        RlpReader ctx = new(data);
        return new LightTransaction(
            timestamp: ctx.DecodeUInt256(),
            sender: ctx.DecodeAddress()!,
            nonce: ctx.DecodeULong(),
            hash: ctx.DecodeKeccak()!,
            value: ctx.DecodeUInt256(),
            gasLimit: ctx.DecodeULong(),
            gasPrice: ctx.DecodeUInt256(),
            maxFeePerGas: ctx.DecodeUInt256(),
            maxFeePerBlobGas: ctx.DecodeUInt256(),
            blobVersionHashes: ctx.DecodeByteArrays(BlobTxDecoder<Transaction>.BlobVersionedHashesCountLimit, innerSize: Hash256.Size),
            poolIndex: ctx.DecodeULong(),
            size: ctx.DecodePositiveInt(),
            proofVersion: ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1 ? (ProofVersion)ctx.ReadByte() : default);
    }
}
