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

    /// <summary>Populates the <c>[chain_id, nonce, max_priority_fee_per_gas, max_fee_per_gas, gas_limit, to, value, data, access_list]</c> fields and returns their length.</summary>
    protected static int PopulateEip1559FieldsLength(Transaction transaction, ref LengthStack lengths) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + GetLegacyFieldsLength(transaction)
        + Rlp.LengthOf(transaction.DecodedMaxFeePerGas)
        + AccessListDecoder.Instance.PopulateLength(transaction.AccessList, ref lengths);

    protected static void EncodeEip1559Fields<TWriter>(Transaction transaction, ref TWriter writer, ref LengthStack lengths)
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
        AccessListDecoder.Instance.Encode(ref writer, transaction.AccessList, ref lengths);
    }
}

public sealed class EIP1559TxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.EIP1559, transactionFactory)
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
        StartTypedTransaction(ref writer, TxType.EIP1559, ref lengths, rlpBehaviors);
        EncodeEip1559Fields(transaction, ref writer, ref lengths);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
    }

    internal static int PopulateTransactionLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning) =>
        PushTypedTransactionLength(
            PopulateEip1559FieldsLength(transaction, ref lengths) + GetTypedSignatureLength(transaction.Signature, forSigning),
            ref lengths, rlpBehaviors);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors) =>
            DecodeEip1559Fields(transaction, ref decoderContext, rlpBehaviors);

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeTypedSignature(transaction, ref decoderContext, rlpBehaviors);
    }
}
