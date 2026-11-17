// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class AccessListTxDecoder(Func<Transaction>? transactionFactory = null) : BaseTxDecoder(TxType.AccessList, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int contentLength = GetContentLength(transaction, forSigning);
        StartTypedTransaction(ref writer, TxType.AccessList, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(transaction.ChainId ?? 0);
        EncodeLegacyFields(transaction, ref writer);
        AccessListDecoder.Instance.Encode(ref writer, transaction.AccessList, rlpBehaviors);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
        writer.EndSequence(sequence);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction, forSigning)), rlpBehaviors);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + GetLegacyFieldsLength(transaction)
        + AccessListDecoder.Instance.GetLength(transaction.AccessList, RlpBehaviors.None)
        + GetTypedSignatureLength(transaction.Signature, forSigning);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag
        {
            transaction.ChainId = reader.DecodeULong<TCanonicalChecks>();
            DecodeLegacyFields<TCanonicalChecks>(transaction, ref reader, source, hasMaxFeePerGas: false);
            transaction.AccessList = AccessListDecoder.Decode<TCanonicalChecks>(ref reader, rlpBehaviors);
        }

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeTypedSignature<TCanonicalChecks>(transaction, ref reader, rlpBehaviors);
    }
}
