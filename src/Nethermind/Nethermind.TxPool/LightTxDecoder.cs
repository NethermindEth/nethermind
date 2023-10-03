// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class LightTxDecoder : TxDecoder<Transaction>
{
    private int GetLength(Transaction tx)
    {
        return Rlp.LengthOf(tx.Timestamp)
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
               + Rlp.LengthOf(tx.GetLength());

    }

    public byte[] Encode(Transaction tx)
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

        return rlpStream.Data!;
    }

    public LightTransaction Decode(byte[] data)
    {
        RlpStream rlpStream = new(data);
        return new LightTransaction(
            rlpStream.DecodeUInt256(),
            rlpStream.DecodeAddress()!,
            rlpStream.DecodeUInt256(),
            rlpStream.DecodeKeccak()!,
            rlpStream.DecodeUInt256(),
            rlpStream.DecodeLong(),
            rlpStream.DecodeUInt256(),
            rlpStream.DecodeUInt256(),
            rlpStream.DecodeUInt256(),
            rlpStream.DecodeByteArrays(),
            rlpStream.DecodeUlong(),
            rlpStream.DecodeInt());
    }
}
