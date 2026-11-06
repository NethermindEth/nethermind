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
    // Format 2 holds the already-derived elided network-encoding size. It is still written when the consensus size
    // is unknown; otherwise records keep the foundational consensus size, from which future encodings can be derived.
    private const byte ElidedNetworkEncodingSizeFormatVersion = 2;

    private static int GetLength(Transaction tx, Address? paymaster, int networkSize, int persistedEncodingSize, byte sizeFormatVersion) => Rlp.LengthOf(tx.Timestamp)
               + Rlp.LengthOf(tx.SenderAddress)
               + Rlp.LengthOf(tx.Nonce)
               + Rlp.LengthOf(tx.Hash)
               + Rlp.LengthOf(tx.Value)
               + Rlp.LengthOf(tx.GasLimit)
               + Rlp.LengthOf(tx.GasPrice)
               + Rlp.LengthOf(tx.DecodedMaxFeePerGas)
               + Rlp.LengthOf(tx.MaxFeePerBlobGas.GetValueOrDefault())
               + Rlp.LengthOf(tx.BlobVersionedHashes!)
               + Rlp.LengthOf(tx.PoolIndex)
               + Rlp.LengthOf(networkSize)
               + Rlp.LengthOf(sizeof(byte))
               + Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0)
               + Rlp.LengthOf(persistedEncodingSize)
               + Rlp.LengthOf(sizeFormatVersion)
               + Rlp.LengthOf((byte)tx.Type)
               + (FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline) ? Rlp.LengthOf(expiryDeadline) : 0)
               + TrailingLength(tx, paymaster);

    /// <summary>Length of the trailing group a frame transaction's record ends in, or zero for any other type.</summary>
    /// <remarks>
    /// One group rather than adjacent optional fields: RLP cannot tell a 20-byte integer from an address, so a
    /// two-key <c>nonce_keys</c> list is byte-identical to a payer pair.
    /// </remarks>
    private static int TrailingLength(Transaction tx, Address? paymaster)
    {
        int content = TrailingContentLength(tx, paymaster);
        return content == 0 ? 0 : Rlp.LengthOfSequence(content);
    }

    /// <summary>Content length of the trailing group, or zero for a record that is not a frame transaction's.</summary>
    /// <remarks>The paymaster is passed in rather than re-derived, so the length pass and the write pass cannot
    /// disagree and over- or under-fill the buffer. Every slot is written, so the group always ends in the key-set flag.
    /// </remarks>
    private static int TrailingContentLength(Transaction tx, Address? paymaster) =>
        !tx.SupportsFrames
            ? 0
            : (tx.NonceKeys is { } nonceKeys ? FrameTxNonceCalldata.KeysLength(nonceKeys) : Rlp.LengthOfSequence(0))
              + Rlp.LengthOf(tx.PayerAddress)
              + Rlp.LengthOf(tx.PayerExposure ?? default)
              + Rlp.LengthOf(paymaster)
              + Rlp.LengthOf(tx.MaxFee)
              + Rlp.LengthOf(tx.NonceKeys is not null);

    public static byte[] Encode(Transaction tx)
    {
        // Read once through the pool's own key, so the record and the cap's ledger cannot disagree on the sponsor.
        Address? paymaster = PendingPaymasterCache.KeyFor(tx);
        int networkSize = tx.GetLength();
        (int persistedEncodingSize, byte sizeFormatVersion) = GetPersistedEncodingSize(tx);
        byte[] bytes = new byte[GetLength(tx, paymaster, networkSize, persistedEncodingSize, sizeFormatVersion)];
        RlpWriter writer = new(bytes);

        writer.Encode(tx.Timestamp);
        writer.Encode(tx.SenderAddress);
        writer.Encode(tx.Nonce);
        writer.Encode(tx.Hash);
        writer.Encode(in tx.ValueRef);
        writer.Encode(tx.GasLimit);
        writer.Encode(tx.GasPrice);
        writer.Encode(tx.DecodedMaxFeePerGas);
        // A frame transaction has no max_fee_per_blob_gas; its max_fee is a trailing slot.
        writer.Encode(tx.MaxFeePerBlobGas.GetValueOrDefault());
        writer.Encode(tx.BlobVersionedHashes!);
        writer.Encode(tx.PoolIndex);
        writer.Encode(networkSize);
        writer.Encode((byte)(tx.GetProofVersion() ?? default));
        EncodeAvailableCellMask(tx, ref writer);
        writer.Encode(persistedEncodingSize);
        writer.Encode(sizeFormatVersion);
        // Appended after the blob fields so records written before it still decode, defaulting to TxType.Blob.
        writer.Encode((byte)tx.Type);
        // Expiry needs the deadline after a reload, where the frames that carried it are gone.
        if (FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline)) writer.Encode(expiryDeadline);
        // The trailing group of a frame transaction, a sequence so the decoder tells it from the expiry deadline that
        // only sometimes precedes it.
        int trailingContent = TrailingContentLength(tx, paymaster);
        if (trailingContent != 0)
        {
            writer.StartSequence(trailingContent);
            if (tx.NonceKeys is { } nonceKeys) FrameTxNonceCalldata.EncodeKeys(nonceKeys, ref writer);
            else writer.StartSequence(0);

            // Null when the payer is not what needs the slot: a record admitted without simulation names a
            // sponsor the cap counts, or a price the sender bound sums, with no payer to reserve against.
            writer.Encode(tx.PayerAddress);
            // The placeholder for a transaction that never reached the exposure gate, read back as absent.
            // A genuine zero price collapses onto it and costs the same: the fallback's fee terms are zero too.
            writer.Encode(tx.PayerExposure ?? default);
            writer.Encode(paymaster);
            writer.Encode(tx.MaxFee);
            // The keys list writes an empty set as it writes none, yet EIP-8250 accepts the one and refuses the other.
            writer.Encode(tx.NonceKeys is not null);
        }

        return bytes;
    }

    /// <summary>Reads a <c>nonce_keys</c> list, mapping the empty one to <c>null</c>; the caller restores an empty set when the record's key-set flag is set.</summary>
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
        byte[][] blobVersionHashes = ctx.DecodeByteArrays(BlobTxDecoder.BlobVersionedHashesCountLimit, innerSize: Hash256.Size);
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
        int consensusEncodingSize = sizeFormatVersion == ConsensusEncodingSizeFormatVersion ? persistedEncodingSize : 0;
        int elidedNetworkEncodingSize = sizeFormatVersion == ElidedNetworkEncodingSizeFormatVersion ? persistedEncodingSize : 0;
        TxType type = optionalFieldCount >= 5 ? (TxType)ctx.DecodeByte() : TxType.Blob;
        // The deadline is the only optional scalar left, so a sequence here means the trailing group follows instead.
        ulong? expiryDeadline = optionalFieldCount >= 6 && !ctx.IsSequenceNext() ? ctx.DecodeULong() : null;
        UInt256[]? nonceKeys = null;
        Address? payerAddress = null;
        UInt256? payerExposure = null;
        Address? paymaster = null;
        UInt256? maxFee = null;
        if (ctx.PeekNumberOfItemsRemaining(maxSearch: 1) == 1)
        {
            int trailingLength = ctx.ReadSequenceLength();
            int end = ctx.Position + trailingLength;
            // Length-checked first: IsSequenceNext is an unguarded index that would read past an empty group.
            // A group not opening with the keys list is the flat nonce_keys list of a record from before max_fee.
            if (trailingLength > 0 && ctx.IsSequenceNext())
            {
                nonceKeys = DecodeKeysOrNull(ref ctx);
                // Nullable: a record admitted without simulation leaves this slot empty.
                if (ctx.Position < end) payerAddress = ctx.DecodeAddressOrNull();
                if (ctx.Position < end)
                {
                    // The placeholder must not read back as a price, or the sender's bound would charge the
                    // record at it rather than at max_fee.
                    UInt256 priced = ctx.DecodeUInt256();
                    if (!priced.IsZero) payerExposure = priced;
                }

                if (ctx.Position < end) paymaster = ctx.DecodeAddressOrNull();
                if (ctx.Position < end) maxFee = ctx.DecodeUInt256();
                // A record from before the flag reads an empty keys list as no set.
                if (ctx.Position < end && ctx.DecodeBool()) nonceKeys ??= [];
            }

            // Anything a later build appended is skipped, so a new slot costs this one that field rather than
            // making every record unreadable.
            ctx.Position = end;
        }

        ctx.Check(data.Length);

        // A frame record without max_fee was written for the per-gas fee shape, which no longer prices it: read as
        // zero it would understate what the transaction can cost, so it is unreadable rather than restored.
        if (type.SupportsFrames() && maxFee is null)
        {
            throw new RlpException($"{nameof(TxType.FrameTx)} record carries no max_fee");
        }

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
            elidedNetworkEncodingSize,
            type,
            expiryDeadline,
            nonceKeys,
            payerAddress,
            payerExposure,
            paymaster,
            maxFee.GetValueOrDefault());
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
