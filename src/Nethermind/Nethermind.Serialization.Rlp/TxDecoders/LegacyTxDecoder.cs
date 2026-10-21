// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
        ulong v = DecodeSignatureItems(ref decoderContext, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
        transaction.Signature = allowEmptySignature && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? null
            : SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
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
