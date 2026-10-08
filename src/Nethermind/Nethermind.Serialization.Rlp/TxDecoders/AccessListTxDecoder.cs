// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract class BaseAccessListTxDecoder(TxType txType, Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(txType, transactionFactory)
{
    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd,
        RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        base.DecodePayload(transaction, ref decoderContext, payloadEnd, rlpBehaviors);
        transaction.AccessList = AccessListDecoder.Instance.Decode(ref decoderContext, rlpBehaviors);
    }
}

public sealed class AccessListTxDecoder(Func<Transaction>? transactionFactory = null) : BaseAccessListTxDecoder(TxType.AccessList, transactionFactory)
{
    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = GetContentLength(transaction, forSigning);
        StartTypedTransaction(ref writer, TxType.AccessList, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        writer.StartSequence(contentLength);
        writer.Encode(transaction.ChainId ?? 0);
        EncodeLegacyFields(transaction, ref writer);
        AccessListDecoder.Instance.Encode(ref writer, transaction.AccessList, rlpBehaviors);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction, forSigning)), rlpBehaviors);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + GetLegacyFieldsLength(transaction)
        + AccessListDecoder.Instance.GetLength(transaction.AccessList, RlpBehaviors.None)
        + GetTypedSignatureLength(transaction.Signature, forSigning);
}
