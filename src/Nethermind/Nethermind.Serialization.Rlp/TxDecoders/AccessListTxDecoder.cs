// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class AccessListTxDecoder(Func<Transaction>? transactionFactory = null) : BaseTxDecoder(TxType.AccessList, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, ref lengths, rlpBehaviors, forSigning);

    public override int PopulateLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        PopulateTransactionLength(transaction, ref lengths, rlpBehaviors, forSigning);

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref TWriter writer, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        StartTypedTransaction(ref writer, TxType.AccessList, ref lengths, rlpBehaviors);
        writer.Encode(transaction.ChainId ?? 0);
        EncodeLegacyFields(transaction, ref writer);
        AccessListDecoder.Instance.Encode(ref writer, transaction.AccessList, ref lengths);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
    }

    internal static int PopulateTransactionLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning) =>
        PushTypedTransactionLength(
            Rlp.LengthOf(transaction.ChainId ?? 0)
            + GetLegacyFieldsLength(transaction)
            + AccessListDecoder.Instance.PopulateLength(transaction.AccessList, ref lengths)
            + GetTypedSignatureLength(transaction.Signature, forSigning),
            ref lengths, rlpBehaviors);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            DecodeLegacyFields(transaction, ref decoderContext, hasMaxFeePerGas: false);
            transaction.AccessList = AccessListDecoder.Instance.Decode(ref decoderContext, rlpBehaviors);
        }

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeTypedSignature(transaction, ref decoderContext, rlpBehaviors);
    }
}
