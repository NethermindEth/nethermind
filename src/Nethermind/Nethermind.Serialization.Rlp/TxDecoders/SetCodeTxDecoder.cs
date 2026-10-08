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

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd,
        RlpBehaviors rlpBehaviors)
    {
        base.DecodePayload(transaction, ref decoderContext, payloadEnd, rlpBehaviors);
        transaction.AuthorizationList = decoderContext.DecodeNonNullArray(AuthTupleDecoder, limit: AuthorizationListLimit);
    }

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = GetContentLength(transaction, forSigning);
        StartTypedTransaction(ref writer, TxType.SetCode, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        writer.StartSequence(contentLength);
        EncodeEip1559Fields(transaction, ref writer, rlpBehaviors);
        AuthTupleDecoder.EncodeArray(ref writer, transaction.AuthorizationList, rlpBehaviors);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction, forSigning)), rlpBehaviors);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        GetEip1559FieldsLength(transaction)
        + (transaction.AuthorizationList is null ? 1 : Rlp.LengthOfSequence(AuthTupleDecoder.GetContentLength(transaction.AuthorizationList, RlpBehaviors.None)))
        + GetTypedSignatureLength(transaction.Signature, forSigning);
}
