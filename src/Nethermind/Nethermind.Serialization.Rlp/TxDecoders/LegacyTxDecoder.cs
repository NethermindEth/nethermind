// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <param name="transactionFactory">Creates the transactions decoded into.</param>
/// <param name="allowEmptySignature">Decodes a <c>v</c> of zero with empty <c>r</c> and <c>s</c> as an unsigned transaction
/// instead of rejecting it, for chains whose legacy history holds such transactions.</param>
public sealed class LegacyTxDecoder(Func<Transaction>? transactionFactory = null, bool allowEmptySignature = false)
    : BaseTxDecoder(TxType.Legacy, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int start = decoderContext.Position;
        try
        {
            if (allowEmptySignature)
            {
                DecodeTransaction<EmptySignatureAllowedPayload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
            }
            else
            {
                DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
            }
        }
        catch (RlpException) when (TryGetWideVError(decoderContext.Data, start, decoderContext.Position, out string? error))
        {
            throw new RlpException(error);
        }
    }

    private static bool IncludeSigChainIdHack(bool isEip155Enabled, ulong chainId) => isEip155Enabled && chainId != 0;

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, forSigning, isEip155Enabled, chainId);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, forSigning, isEip155Enabled, chainId);

    internal static int GetTransactionLength(Transaction transaction, bool forSigning, bool isEip155Enabled, ulong chainId) =>
        Rlp.LengthOfSequence(GetContentLength(transaction, forSigning, isEip155Enabled, chainId));

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref TWriter writer, bool forSigning, bool isEip155Enabled, ulong chainId)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(GetContentLength(transaction, forSigning, isEip155Enabled, chainId));
        EncodeLegacyFields(transaction, ref writer);

        if (!forSigning)
        {
            Signature? signature = transaction.Signature;
            EncodeSignature(signature, signature?.V ?? 0, ref writer);
        }
        else if (IncludeSigChainIdHack(isEip155Enabled, chainId))
        {
            writer.Encode(chainId);
            writer.Encode(Rlp.OfEmptyByteArray);
            writer.Encode(Rlp.OfEmptyByteArray);
        }
    }

    private static int GetContentLength(Transaction transaction, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        int contentLength = GetLegacyFieldsLength(transaction);

        if (!forSigning)
        {
            Signature? signature = transaction.Signature;
            contentLength += GetSignatureLength(signature, signature?.V ?? 0);
        }
        else if (IncludeSigChainIdHack(isEip155Enabled, chainId))
        {
            contentLength += Rlp.LengthOf(chainId) + 2;
        }

        return contentLength;
    }

    private static void DecodeSignature(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors, bool allowEmptySignature)
    {
        ulong v = DecodeSignatureItems(ref decoderContext, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
        transaction.Signature = allowEmptySignature && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? null
            : SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    /// <summary>
    /// Names the error of the transaction at <paramref name="start"/> when its decoding failed at <paramref name="failedAt"/>
    /// on a <c>v</c> too wide for a <see cref="ulong"/>.
    /// </summary>
    /// <remarks>
    /// Only a <c>v</c> in its own slot counts: the seventh item, followed by <c>r</c> and <c>s</c> that end the transaction.
    /// EIP-155 sets <c>v = 35 + 2 * chainId + parity</c>, so such a <c>v</c> can still hold a 64-bit chain id, which is
    /// only one no supported network has. A chain id wider than 64 bits makes <c>v</c> itself invalid.
    /// </remarks>
    private static bool TryGetWideVError(ReadOnlySpan<byte> data, int start, int failedAt, [NotNullWhen(true)] out string? error)
    {
        error = null;
        (int prefixLength, int contentLength) = RlpHelpers.PeekPrefixAndContentLength(data, start);
        int end = start + prefixLength + contentLength;
        if (end > data.Length)
        {
            return false;
        }

        int position = start + prefixLength;
        for (int i = 0; i < 6 && position < failedAt; i++)
        {
            position += RlpHelpers.PeekNextRlpLength(data, position);
        }

        if (position != failedAt
            || !TrySkipSignatureItem(data, ref position, end, out int vLength)
            || vLength <= sizeof(ulong)
            || data[position - vLength] == 0
            || !TrySkipSignatureItem(data, ref position, end, out _)
            || !TrySkipSignatureItem(data, ref position, end, out _)
            || position != end)
        {
            return false;
        }

        RlpHelpers.DecodeUInt256(data, failedAt, out UInt256 v);
        UInt256 chainId = (v + v % 2 - 36) / 2;
        error = chainId <= ulong.MaxValue
            ? $"InvalidTxChainId: Chain id {chainId} in v is not supported."
            : $"{TxErrorMessages.InvalidTxSignature} Chain id in v exceeds uint64.";
        return true;
    }

    /// <summary>Moves past the byte string of at most 32 bytes at <paramref name="position"/> that ends by <paramref name="end"/>.</summary>
    private static bool TrySkipSignatureItem(ReadOnlySpan<byte> data, ref int position, int end, out int contentLength)
    {
        contentLength = 0;
        if (position >= end || data[position] >= Rlp.EmptyListByte)
        {
            return false;
        }

        (int prefixLength, contentLength) = RlpHelpers.PeekPrefixAndContentLength(data, position);
        position += prefixLength + contentLength;
        return contentLength <= 32 && position <= end;
    }

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors) =>
            DecodeLegacyFields(transaction, ref decoderContext, hasMaxFeePerGas: false);

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeSignature(transaction, ref decoderContext, rlpBehaviors, allowEmptySignature: false);
    }

    private readonly struct EmptySignatureAllowedPayload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors) =>
            DecodeLegacyFields(transaction, ref decoderContext, hasMaxFeePerGas: false);

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeSignature(transaction, ref decoderContext, rlpBehaviors, allowEmptySignature: true);
    }
}
