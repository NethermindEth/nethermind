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
        if (allowEmptySignature)
        {
            DecodeTransaction<EmptySignatureAllowedPayload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
        }
        else
        {
            DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
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
        int vPosition = decoderContext.Position;
        try
        {
            ulong v = DecodeSignatureItems(ref decoderContext, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
            transaction.Signature = allowEmptySignature && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
                ? null
                : SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
        }
        catch (RlpException) when (TryGetWideVError(decoderContext.Data, vPosition, out string? error))
        {
            throw new RlpException(error);
        }
    }

    /// <summary>Names the error of a <c>v</c> at <paramref name="position"/> too wide for a <see cref="ulong"/>.</summary>
    /// <remarks>
    /// EIP-155 sets <c>v = 35 + 2 * chainId + parity</c>, so such a <c>v</c> can still hold a 64-bit chain id, which is
    /// only one no supported network has. A chain id wider than 64 bits makes <c>v</c> itself invalid.
    /// </remarks>
    private static bool TryGetWideVError(ReadOnlySpan<byte> data, int position, [NotNullWhen(true)] out string? error)
    {
        error = null;
        if (data[position] >= Rlp.EmptyListByte)
        {
            return false;
        }

        (int prefixLength, int contentLength) = RlpHelpers.PeekPrefixAndContentLength(data, position);
        if (contentLength is <= sizeof(ulong) or > 32 || data[position + prefixLength] == 0)
        {
            return false;
        }

        RlpHelpers.DecodeUInt256(data, position, out UInt256 v);
        UInt256 chainId = (v + v % 2 - 36) / 2;
        error = chainId <= ulong.MaxValue
            ? $"InvalidTxChainId: Chain id {chainId} in v is not supported."
            : $"{TxErrorMessages.InvalidTxSignature} Chain id in v exceeds uint64.";
        return true;
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
