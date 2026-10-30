// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <param name="transactionFactory">Creates the transactions decoded into.</param>
/// <param name="allowEmptySignature">Decodes a <c>v</c> of zero with empty <c>r</c> and <c>s</c> as an unsigned transaction
/// instead of rejecting it, for chains whose legacy history holds such transactions.</param>
public sealed class LegacyTxDecoder(Func<Transaction>? transactionFactory = null, bool allowEmptySignature = false)
    : BaseTxDecoder(TxType.Legacy, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (allowEmptySignature)
        {
            DecodeTransaction<EmptySignatureAllowedPayload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
        }
        else
        {
            DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
        }
    }

    private static bool IncludeSigChainIdHack(bool isEip155Enabled, ulong chainId) => isEip155Enabled && chainId != 0;

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, forSigning, isEip155Enabled, chainId);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, forSigning, isEip155Enabled, chainId);

    internal static int GetTransactionLength(Transaction transaction, bool forSigning, bool isEip155Enabled, ulong chainId) =>
        Rlp.LengthOfSequence(GetContentLength(transaction, forSigning, isEip155Enabled, chainId));

    internal static void EncodeTransaction(Transaction transaction, ref RlpWriter writer, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        RlpSequence sequence = writer.StartSequence(GetContentLength(transaction, forSigning, isEip155Enabled, chainId));
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

        writer.EndSequence(sequence);
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

    private static void DecodeSignature<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors, bool allowEmptySignature)
        where TCanonicalChecks : struct, IFlag
    {
        ulong v = DecodeSignatureItems<TCanonicalChecks>(ref reader, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
        transaction.Signature = allowEmptySignature && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? null
            : SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeLegacyFields<TCanonicalChecks>(transaction, ref reader, source, hasMaxFeePerGas: false);

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeSignature<TCanonicalChecks>(transaction, ref reader, rlpBehaviors, allowEmptySignature: false);
    }

    private readonly struct EmptySignatureAllowedPayload : ITxPayloadDecoder
    {
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeLegacyFields<TCanonicalChecks>(transaction, ref reader, source, hasMaxFeePerGas: false);

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeSignature<TCanonicalChecks>(transaction, ref reader, rlpBehaviors, allowEmptySignature: true);
    }
}
