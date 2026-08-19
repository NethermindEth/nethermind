// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
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
               + Rlp.LengthOf(sizeof(byte))
               + Rlp.LengthOf((byte)tx.Type)
               + (FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline) ? Rlp.LengthOf(expiryDeadline) : 0)
               + (tx.PayerAddress is null ? 0 : Rlp.LengthOfSequence(PayerContentLength(tx)));

    private static int PayerContentLength(Transaction tx) =>
        Rlp.LengthOf(tx.PayerAddress) + Rlp.LengthOf(tx.PayerExposure ?? default);

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
        // Appended last so records written before it still decode, defaulting to TxType.Blob.
        writer.Encode((byte)tx.Type);
        // Expiry needs the deadline after a reload, where the frames that carried it are gone.
        if (FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline)) writer.Encode(expiryDeadline);
        // EIP-8141: the pool sums its per-payer bound over the pending set, which survives a restart in this
        // db — so the reservation each record holds has to survive with it, or the bound stops covering it.
        // A sequence, so the decoder tells it apart from the expiry deadline that only sometimes precedes it,
        // and written only for a resolved payer, so a plain blob record keeps its exact layout.
        if (tx.PayerAddress is not null)
        {
            writer.StartSequence(PayerContentLength(tx));
            writer.Encode(tx.PayerAddress);
            // A resolved payer that never reached the exposure gate holds no reservation, which this record
            // does not distinguish from a zero one: reserving, releasing and restoring zero are all no-ops.
            writer.Encode(tx.PayerExposure ?? default);
        }

        return bytes;
    }

    public static LightTransaction Decode(byte[] data)
    {
        RlpReader ctx = new(data);
        // Read as statements, not as arguments: the trailing fields below need the reader's remaining count
        // between reads, which an argument list's left-to-right evaluation cannot express.
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
        ProofVersion proofVersion = ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1 ? (ProofVersion)ctx.DecodeByte() : default;
        TxType type = ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1 ? (TxType)ctx.DecodeByte() : TxType.Blob;

        // Both trailing groups are optional, so the deadline is recognized by being the scalar of the two:
        // only the payer group is a sequence, which is what keeps the two apart when either is absent.
        ulong? expiryDeadline = ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1 && !ctx.IsSequenceNext()
            ? ctx.DecodeULong()
            : null;

        Address? payerAddress = null;
        UInt256? payerExposure = null;
        if (ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1)
        {
            ctx.ReadSequenceLength();
            payerAddress = ctx.DecodeAddress();
            payerExposure = ctx.DecodeUInt256();
        }

        return new LightTransaction(timestamp, sender, nonce, hash, value, gasLimit, gasPrice, maxFeePerGas,
            maxFeePerBlobGas, blobVersionHashes, poolIndex, size, proofVersion, type, expiryDeadline,
            payerAddress, payerExposure);
    }
}
