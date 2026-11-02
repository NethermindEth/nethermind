// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract class BaseEIP1559TxDecoder(TxType txType, Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(txType, transactionFactory)
{
    /// <summary>Decodes the <c>[chain_id, nonce, max_priority_fee_per_gas, max_fee_per_gas, gas_limit, to, value, data, access_list]</c> fields.</summary>
    protected static void DecodeEip1559Fields(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        DecodeLegacyFields(transaction, ref decoderContext, hasMaxFeePerGas: true);
        transaction.AccessList = AccessListDecoder.Instance.Decode(ref decoderContext, rlpBehaviors);
    }

    /// <summary>The length of the <c>[chain_id, nonce, max_priority_fee_per_gas, max_fee_per_gas, gas_limit, to, value, data, access_list]</c> fields.</summary>
    protected static int GetEip1559FieldsLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + GetLegacyFieldsLength(transaction)
        + Rlp.LengthOf(transaction.DecodedMaxFeePerGas)
        + AccessListDecoder.Instance.GetLength(transaction.AccessList, RlpBehaviors.None);

    protected static void EncodeEip1559Fields<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(transaction.ChainId ?? 0);
        writer.Encode(transaction.Nonce);
        writer.Encode(transaction.GasPrice);
        writer.Encode(transaction.DecodedMaxFeePerGas);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.To);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.Data);
        AccessListDecoder.Instance.Encode(ref writer, transaction.AccessList, rlpBehaviors);
    }
}

public sealed class EIP1559TxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.EIP1559, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = GetContentLength(transaction, forSigning);
        StartTypedTransaction(ref writer, TxType.EIP1559, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        writer.StartSequence(contentLength);
        EncodeEip1559Fields(transaction, ref writer, rlpBehaviors);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction, forSigning)), rlpBehaviors);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        GetEip1559FieldsLength(transaction) + GetTypedSignatureLength(transaction.Signature, forSigning);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors) =>
            DecodeEip1559Fields(transaction, ref decoderContext, rlpBehaviors);

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeTypedSignature(transaction, ref decoderContext, rlpBehaviors);
    }
}
