// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class LightTxDecoder : TxDecoder<Transaction>
{
    private static int GetLength(Transaction tx) => Rlp.LengthOf(tx.Timestamp)
               + Rlp.LengthOf(tx.SenderAddress)
               + Rlp.LengthOf(tx.Nonce)
               + Rlp.LengthOf(tx.Hash)
               + Rlp.LengthOf(in tx.ValueRef)
               + Rlp.LengthOf(tx.GasLimit)
               + Rlp.LengthOf(tx.GasPrice)
               + Rlp.LengthOf(tx.DecodedMaxFeePerGas)
               + Rlp.LengthOf(tx.MaxFeePerBlobGas!.Value)
               + Rlp.LengthOf(tx.BlobVersionedHashes!)
               + Rlp.LengthOf(tx.PoolIndex)
               + Rlp.LengthOf(tx.GetLength())
               + Rlp.LengthOf(sizeof(byte))
               + Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0);

    public static byte[] Encode(Transaction tx)
    {
        RlpStream rlpStream = new(GetLength(tx));

        rlpStream.Encode(tx.Timestamp);
        rlpStream.Encode(tx.SenderAddress);
        rlpStream.Encode(tx.Nonce);
        rlpStream.Encode(tx.Hash);
        rlpStream.Encode(in tx.ValueRef);
        rlpStream.Encode(tx.GasLimit);
        rlpStream.Encode(tx.GasPrice);
        rlpStream.Encode(tx.DecodedMaxFeePerGas);
        rlpStream.Encode(tx.MaxFeePerBlobGas!.Value);
        rlpStream.Encode(tx.BlobVersionedHashes!);
        rlpStream.Encode(tx.PoolIndex);
        rlpStream.Encode(tx.GetLength());
        rlpStream.Encode((byte)((tx.NetworkWrapper as ShardBlobNetworkWrapper)?.Version ?? default));
        EncodeAvailableCellMask(tx, rlpStream);

        return rlpStream.Data.ToArray()!;
    }

    public static LightTransaction Decode(byte[] data)
    {
        Rlp.ValueDecoderContext ctx = new(data);
        return new LightTransaction(
            ctx.DecodeUInt256(),
            ctx.DecodeAddress()!,
            ctx.DecodeUInt256(),
            ctx.DecodeKeccak()!,
            ctx.DecodeUInt256(),
            ctx.DecodeLong(),
            ctx.DecodeUInt256(),
            ctx.DecodeUInt256(),
            ctx.DecodeUInt256(),
            ctx.DecodeByteArrays(innerSize: Hash256.Size),
            ctx.DecodeULong(),
            ctx.DecodePositiveInt(),
            ctx.PeekNumberOfItemsRemaining(maxSearch: 2) >= 1 ? (ProofVersion)ctx.ReadByte() : default,
            ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1
                ? BlobCellMask.FromBytes(ctx.DecodeByteArraySpan())
                : default);
    }

    private static void EncodeAvailableCellMask(Transaction tx, RlpStream rlpStream)
    {
        Span<byte> bytes = stackalloc byte[BlobCellMask.FixedByteLength];
        GetAvailableCellMask(tx).WriteTo(bytes);
        rlpStream.Encode(bytes);
    }

    private static BlobCellMask GetAvailableCellMask(Transaction tx) =>
        tx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            ? wrapper.GetAvailableCellMask()
            : BlobCellMask.Empty;
}
