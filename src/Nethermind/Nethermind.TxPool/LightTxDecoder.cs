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
        RlpStream rlpStream = new(GetLength(tx));

        rlpStream.Encode(tx.Timestamp);
        rlpStream.Encode(tx.SenderAddress);
        rlpStream.Encode(tx.Nonce);
        rlpStream.Encode(tx.Hash);
        rlpStream.Encode(tx.Value);
        rlpStream.Encode(tx.GasLimit);
        rlpStream.Encode(tx.GasPrice);
        rlpStream.Encode(tx.DecodedMaxFeePerGas);
        rlpStream.Encode(tx.MaxFeePerBlobGas!.Value);
        rlpStream.Encode(tx.BlobVersionedHashes!);
        rlpStream.Encode(tx.PoolIndex);
        rlpStream.Encode(tx.GetLength());
        rlpStream.Encode((byte)((tx.NetworkWrapper as ShardBlobNetworkWrapper)?.Version ?? default));

        return rlpStream.Data.ToArray()!;
    }

    public static LightTransaction Decode(byte[] data)
    {
        Rlp.ValueDecoderContext ctx = new(data);
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
