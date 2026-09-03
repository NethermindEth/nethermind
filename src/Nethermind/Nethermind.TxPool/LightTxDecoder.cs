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

    private static int GetLength(Transaction tx, Address? paymaster) => Rlp.LengthOf(tx.Timestamp)
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
               + Rlp.LengthOf(ConsensusEncodingSizeFormatVersion)
               + Rlp.LengthOf((byte)tx.Type)
               + (FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline) ? Rlp.LengthOf(expiryDeadline) : 0)
               + TrailingLength(tx, paymaster);

    /// <summary>
    /// Length of the single optional trailing group, or zero when the record needs none.
    /// </summary>
    /// <remarks>
    /// One group rather than two adjacent optional sequences: RLP cannot tell a 20-byte integer from an
    /// address, so a two-key <c>nonce_keys</c> list is byte-identical to a payer pair.
    /// </remarks>
    private static int TrailingLength(Transaction tx, Address? paymaster)
    {
        int content = TrailingContentLength(tx, paymaster);
        return content == 0
            ? tx.NonceKeys is { } keysOnly ? FrameTxNonceCalldata.KeysLength(keysOnly) : 0
            : Rlp.LengthOfSequence(content);
    }

    /// <summary>Content length of the grouped form, or zero for a record that needs neither payer nor paymaster.</summary>
    /// <remarks>The paymaster is passed in rather than re-derived, so the length pass and the write pass cannot
    /// disagree and over- or under-fill the buffer.</remarks>
    private static int TrailingContentLength(Transaction tx, Address? paymaster)
    {
        if (tx.PayerAddress is null && paymaster is null) return 0;

        // Slot 0 is always the keys list, so its sequence header is what tells this form from the flat
        // nonce_keys list a groupless record still writes, whose first element is a scalar.
        return (tx.NonceKeys is { } nonceKeys ? FrameTxNonceCalldata.KeysLength(nonceKeys) : Rlp.LengthOfSequence(0))
               + Rlp.LengthOf(tx.PayerAddress)
               + Rlp.LengthOf(tx.PayerExposure ?? default)
               + (paymaster is null ? 0 : Rlp.LengthOf(paymaster));
    }

    public static byte[] Encode(Transaction tx)
    {
        // Read once through the pool's own key, so the record and the cap's ledger cannot disagree on the sponsor.
        Address? paymaster = PendingPaymasterCache.KeyFor(tx);
        byte[] bytes = new byte[GetLength(tx, paymaster)];
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
        writer.Encode((byte)(tx.GetProofVersion() ?? default));
        EncodeAvailableCellMask(tx, ref writer);
        writer.Encode(GetConsensusEncodingSize(tx));
        writer.Encode(ConsensusEncodingSizeFormatVersion);
        // Appended after the blob fields so records written before it still decode, defaulting to TxType.Blob.
        writer.Encode((byte)tx.Type);
        // Expiry needs the deadline after a reload, where the frames that carried it are gone.
        if (FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline)) writer.Encode(expiryDeadline);
        // One optional trailing group, a sequence so the decoder tells it from the expiry deadline that only
        // sometimes precedes it. Written whole, so the keys list is present even when empty.
        int trailingContent = TrailingContentLength(tx, paymaster);
        if (trailingContent == 0)
        {
            // Nothing to group: a keys-only record keeps the exact bytes every earlier build writes.
            if (tx.NonceKeys is { } keysOnly) FrameTxNonceCalldata.EncodeKeys(keysOnly, ref writer);
        }
        else
        {
            writer.StartSequence(trailingContent);
            if (tx.NonceKeys is { } nonceKeys) FrameTxNonceCalldata.EncodeKeys(nonceKeys, ref writer);
            else writer.StartSequence(0);

            // Null when only the paymaster needs the group: a record admitted without simulation names a
            // sponsor the cap counts, but has no resolved payer to reserve against.
            writer.Encode(tx.PayerAddress);
            // A payer that never reached the exposure gate holds no reservation, which this record does not
            // distinguish from a zero one: reserving, releasing and restoring zero are all no-ops.
            writer.Encode(tx.PayerExposure ?? default);
            if (paymaster is not null) writer.Encode(paymaster);
        }

        return bytes;
    }

    /// <summary>Reads a <c>nonce_keys</c> list, mapping the empty one to <c>null</c> as the record means it.</summary>
    private static UInt256[]? DecodeKeysOrNull(ref RlpReader ctx)
    {
        UInt256[] keys = FrameTxNonceCalldata.DecodeKeys(ref ctx);
        return keys.Length == 0 ? null : keys;
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

        int optionalFieldCount = ctx.PeekNumberOfItemsRemaining(maxSearch: 8);
        if (optionalFieldCount > 7)
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
        TxType type = optionalFieldCount >= 5 ? (TxType)ctx.DecodeByte() : TxType.Blob;
        // The deadline is the only optional scalar left, so a sequence here means the keys follow instead.
        ulong? expiryDeadline = optionalFieldCount >= 6 && !ctx.IsSequenceNext() ? ctx.DecodeULong() : null;
        UInt256[]? nonceKeys = null;
        Address? payerAddress = null;
        UInt256? payerExposure = null;
        Address? paymaster = null;
        if (ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1)
        {
            // Legacy records end in a flat nonce_keys list, whose first element is a scalar; the grouped form
            // always opens with the keys list. That is total, since a key can never encode as a sequence.
            int groupStart = ctx.Position;
            int trailingLength = ctx.ReadSequenceLength();
            int end = ctx.Position + trailingLength;
            // Length-checked first: an empty group is a legacy empty list, and IsSequenceNext is an
            // unguarded index that would read past the buffer for it.
            if (trailingLength > 0 && ctx.IsSequenceNext())
            {
                nonceKeys = DecodeKeysOrNull(ref ctx);
                // Nullable: a group written for the paymaster alone leaves this slot empty.
                if (ctx.Position < end) payerAddress = ctx.DecodeAddressOrNull();
                if (ctx.Position < end) payerExposure = ctx.DecodeUInt256();
                // Nullable for the same reason as the payer: once a later slot exists, an absent paymaster
                // is written as the placeholder rather than omitted.
                if (ctx.Position < end) paymaster = ctx.DecodeAddressOrNull();
                // Anything a later build appended is skipped, so a new slot costs this one that field
                // rather than making every grouped record unreadable.
                ctx.Position = end;
            }
            else
            {
                // Rewound so the same decoder reads the flat list, whose header this group's was.
                ctx.Position = groupStart;
                nonceKeys = DecodeKeysOrNull(ref ctx);
            }
        }

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
            type,
            expiryDeadline,
            nonceKeys,
            payerAddress,
            payerExposure,
            paymaster);
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

    private static int GetConsensusEncodingSize(Transaction tx) =>
        tx is LightTransaction lightTx && lightTx.GetConsensusEncodingSize() > 0
            ? lightTx.GetConsensusEncodingSize()
            : tx.GetLength(shouldCountBlobs: false);
}
