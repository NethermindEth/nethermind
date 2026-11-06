// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract class BaseEIP1559TxDecoder(TxType txType, Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(txType, transactionFactory)
{
    /// <summary>Decodes the <c>[chain_id, nonce, max_priority_fee_per_gas, max_fee_per_gas, gas_limit, to, value, data, access_list]</c> fields.</summary>
    protected static void DecodeEip1559Fields<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        transaction.ChainId = reader.DecodeULong<TCanonicalChecks>();
        DecodeLegacyFields<TCanonicalChecks>(transaction, ref reader, source, hasMaxFeePerGas: true);
        transaction.AccessList = AccessListDecoder.Decode<TCanonicalChecks>(ref reader, rlpBehaviors);
    }

    /// <summary>The length of the <c>[chain_id, nonce, max_priority_fee_per_gas, max_fee_per_gas, gas_limit, to, value, data, access_list]</c> fields.</summary>
    protected static int GetEip1559FieldsLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + GetLegacyFieldsLength(transaction)
        + Rlp.LengthOf(transaction.DecodedMaxFeePerGas)
        + AccessListDecoder.Instance.GetLength(transaction.AccessList, RlpBehaviors.None);

    protected static void EncodeEip1559Fields(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors)
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
        StartTypedTransaction(ref writer, TxType.EIP1559, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        RlpSequence sequence = writer.StartSequence(contentLength);
        EncodeEip1559Fields(transaction, ref writer, rlpBehaviors);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
        writer.EndSequence(sequence);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction, forSigning)), rlpBehaviors);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        GetEip1559FieldsLength(transaction) + GetTypedSignatureLength(transaction.Signature, forSigning);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeEip1559Fields<TCanonicalChecks>(transaction, ref reader, source, rlpBehaviors);

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeTypedSignature<TCanonicalChecks>(transaction, ref reader, rlpBehaviors);
    }
}
