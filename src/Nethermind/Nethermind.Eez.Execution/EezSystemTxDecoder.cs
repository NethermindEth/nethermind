// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Eez.Execution;

/// <summary>
/// Codec for <c>0x76 || rlp([chainId, nonce, to, value, input])</c>. The body carries no signature, sender, gas or
/// fee fields; decoding supplies their protocol values so every node derives the same transaction and hash. The
/// target names the execution manager, and with it the gas: rollup0's fixed budget, or RollupX's cap, which
/// execution narrows to what the block has left. Whether the target belongs to the chain is checked at execution.
/// </summary>
public sealed class EezSystemTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseTxDecoder(EezConstants.SystemTxType, transactionFactory is null ? (static () => new T()) : transactionFactory) where T : Transaction, new()
{
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetPayloadLength(transaction);
        StartTypedTransaction(ref writer, EezConstants.SystemTxType, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        writer.StartSequence(contentLength);
        writer.Encode(transaction.ChainId ?? 0);
        writer.Encode(transaction.Nonce);
        writer.Encode(transaction.To);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.Data);
    }

    private static int GetPayloadLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + Rlp.LengthOf(transaction.Nonce)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.ValueRef)
        + Rlp.LengthOf(transaction.Data);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false,
        bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetPayloadLength(transaction)), rlpBehaviors);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd,
            RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.Nonce = decoderContext.DecodeULong();
            decoderContext.DecodeAddressStructRefNonNull(out AddressStructRef to);
            if (to == EezConstants.Eezl2Address)
            {
                transaction.To = EezConstants.Eezl2Address;
                transaction.GasLimit = EezConstants.SystemTxGasLimit;
            }
            else if (to == EezConstants.RollupXEezl2Address)
            {
                transaction.To = EezConstants.RollupXEezl2Address;
                transaction.GasLimit = EezChainRules.RollupXSystemTxGasCap;
            }
            else
            {
                throw new RlpException($"EEZ system transaction must target {EezConstants.Eezl2Address} or {EezConstants.RollupXEezl2Address}.");
            }

            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArrayMemory(DataRlpLimit);
            transaction.SenderAddress = EezConstants.SystemAddress;
            transaction.GasPrice = 0;
            transaction.DecodedMaxFeePerGas = 0;
            transaction.Signature = null;
        }

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            throw new RlpException("EEZ system transaction carries unexpected trailing fields.");
    }
}
