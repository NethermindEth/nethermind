// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Int256;
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
        RlpReader signatureItems = reader;
        ulong v;
        scoped ReadOnlySpan<byte> rBytes, sBytes;
        try
        {
            v = DecodeSignatureItems<TCanonicalChecks>(ref reader, out rBytes, out sBytes);
        }
        catch (RlpReaderException e) when (TryGetWideVError(signatureItems, out string? error))
        {
            throw new RlpReaderException(error, e);
        }

        transaction.Signature = allowEmptySignature && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? null
            : SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    /// <summary>
    /// Names the error of a transaction whose <c>[v, r, s]</c> items start at <paramref name="signatureItems"/> when its
    /// <c>v</c> is too wide for a <see cref="ulong"/>.
    /// </summary>
    /// <remarks>
    /// Only a <c>v</c> in its own slot counts: the seventh item, followed by <c>r</c> and <c>s</c> that end the transaction.
    /// EIP-155 sets <c>v = 35 + 2 * chainId + parity</c>, so such a <c>v</c> can still hold a 64-bit chain id, which is
    /// only one no supported network has. A chain id wider than 64 bits makes <c>v</c> itself invalid.
    /// </remarks>
    private static bool TryGetWideVError(RlpReader signatureItems, [NotNullWhen(true)] out string? error)
    {
        error = null;
        if (!TrySkipSignatureItem(ref signatureItems, out ReadOnlySpan<byte> vBytes)
            || vBytes.Length <= sizeof(ulong)
            || vBytes[0] == 0
            || !TrySkipSignatureItem(ref signatureItems, out _)
            || !TrySkipSignatureItem(ref signatureItems, out _)
            || signatureItems.HasNext)
        {
            return false;
        }

        UInt256 v = new(vBytes, isBigEndian: true);
        UInt256 chainId = (v + v % 2 - 36) / 2;
        error = chainId <= ulong.MaxValue
            ? $"InvalidTxChainId: Chain id {chainId} in v is not supported."
            : $"{TxErrorMessages.InvalidTxSignature} Chain id in v exceeds uint64.";
        return true;
    }

    /// <summary>Moves past the byte string of at most 32 bytes that <paramref name="reader"/> is at.</summary>
    private static bool TrySkipSignatureItem(ref RlpReader reader, out ReadOnlySpan<byte> content)
    {
        content = default;
        if (!reader.HasNext || reader.IsNextSequence)
        {
            return false;
        }

        content = reader.ReadBytes<OffFlag>();
        return content.Length <= 32;
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
