// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class SetCodeTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.SetCode, transactionFactory)
{
    private static RlpLimit AuthorizationListLimit => RlpLimit.For<Transaction>(
        checked((int)(RlpLimit.MaxBlockGas / GasCostOf.PerAuthBaseCost + 1)),
        nameof(Transaction.AuthorizationList)
    );

    private static readonly AuthorizationTupleDecoder AuthTupleDecoder = AuthorizationTupleDecoder.Instance;

    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);

    public override void PopulateLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        PopulateTransactionLength(transaction, ref lengths, rlpBehaviors, forSigning);

    public override void Encode<TWriter>(Transaction transaction, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref lengths, ref writer, rlpBehaviors, forSigning);

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        StartTypedTransaction(ref writer, TxType.SetCode, ref lengths, rlpBehaviors);
        EncodeEip1559Fields(transaction, ref lengths, ref writer);
        AuthTupleDecoder.EncodeSequence(transaction.AuthorizationList, ref lengths, ref writer);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
    }

    internal static void PopulateTransactionLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int contentLength = AuthTupleDecoder.PopulateSequenceAndPeek(transaction.AuthorizationList, ref lengths);
        contentLength += PopulateEip1559FieldsLength(transaction, ref lengths);
        PushTypedTransactionLength(contentLength + GetTypedSignatureLength(transaction.Signature, forSigning), ref lengths, rlpBehaviors);
    }

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors)
        {
            DecodeEip1559Fields(transaction, ref decoderContext, rlpBehaviors);
            transaction.AuthorizationList = decoderContext.DecodeNonNullArray(AuthTupleDecoder, limit: AuthorizationListLimit);
        }

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeTypedSignature(transaction, ref decoderContext, rlpBehaviors);
    }
}
