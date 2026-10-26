// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Eez.Execution;

/// <summary>Encodes the native-v1 envelope <c>0x45 || rlp([version, chainId, sourceHash, gasLimit, to, value, input])</c>.</summary>
/// <remarks>Envelope validation does not authorize a native occurrence or its execution position.</remarks>
public sealed class EezNativeSystemTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(EezConstants.NativeSystemTxType, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        try
        {
            DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors & ~RlpBehaviors.AllowExtraBytes);
            decoderContext.Check(txSequenceStart + transactionSequence.Length);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new RlpException("Native system transaction RLP is truncated.", exception);
        }
    }

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        ValidateEnvelope(transaction, forSigning);
        int contentLength = GetPayloadLength(transaction);
        StartTypedTransaction(ref writer, EezConstants.NativeSystemTxType, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        writer.StartSequence(contentLength);
        writer.Encode(EezConstants.NativeSystemTxVersion);
        writer.Encode(transaction.ChainId!.Value);
        writer.Encode(transaction.SourceHash);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.To);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.Data);
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        ValidateEnvelope(transaction, forSigning);
        return GetTypedTransactionLength(Rlp.LengthOfSequence(GetPayloadLength(transaction)), rlpBehaviors);
    }

    private static int GetPayloadLength(Transaction transaction) =>
        Rlp.LengthOf(EezConstants.NativeSystemTxVersion)
        + Rlp.LengthOf(transaction.ChainId!.Value)
        + Rlp.LengthOf(transaction.SourceHash)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.ValueRef)
        + Rlp.LengthOf(transaction.Data);

    private static void ValidateEnvelope(Transaction transaction, bool forSigning = false)
    {
        if (forSigning) throw new RlpException("Native system transactions cannot be signed.");
        if (transaction.ChainId is null || transaction.SourceHash is null)
            throw new RlpException("Native system transaction chain ID and source hash are required.");
        ulong cap;
        if (transaction.To == EezConstants.RollupXEezl2Address)
            cap = EezChainRules.RollupXSystemTxGasCap;
        else if (transaction.To == EezConstants.L1HistoryAddress && transaction.ValueRef.IsZero)
            cap = EezConstants.L1HistoryTxGasCap;
        else
            throw new RlpException("Native system transaction destination or value is invalid.");
        if (transaction.GasLimit > cap)
            throw new RlpException("Native system transaction exceeds its gas cap.");
    }

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors)
        {
            if (decoderContext.DecodeULong() != EezConstants.NativeSystemTxVersion)
                throw new RlpException("Unsupported native system transaction version.");
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.SourceHash = decoderContext.DecodeKeccak();
            transaction.GasLimit = decoderContext.DecodeULong();
            decoderContext.DecodeAddressStructRefNonNull(out AddressStructRef to);
            if (to == EezConstants.RollupXEezl2Address)
                transaction.To = EezConstants.RollupXEezl2Address;
            else if (to == EezConstants.L1HistoryAddress)
                transaction.To = EezConstants.L1HistoryAddress;
            else
                throw new RlpException("Native system transaction destination is invalid.");
            transaction.Value = decoderContext.DecodeUInt256();
            ValidateEnvelope(transaction);
            transaction.Data = decoderContext.DecodeByteArrayMemory(DataRlpLimit);
            decoderContext.Check(payloadEnd);
            transaction.SenderAddress = Address.SystemUser;
            transaction.Nonce = 0;
            transaction.GasPrice = 0;
            transaction.DecodedMaxFeePerGas = 0;
            transaction.Signature = null;
        }

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            throw new RlpException("Native system transaction carries unexpected trailing fields.");
    }
}
