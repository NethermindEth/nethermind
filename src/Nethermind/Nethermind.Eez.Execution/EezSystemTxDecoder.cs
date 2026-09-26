// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Eez.Execution;

/// <summary>
/// Codec for <c>0x76 || rlp([chainId, nonce, to, value, input])</c>. The body carries no signature, sender, gas or
/// fee fields; decoding supplies their protocol values so every node derives the same transaction and hash.
/// </summary>
public sealed class EezSystemTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseAccessListTxDecoder<T>(EezConstants.SystemTxType, transactionFactory) where T : Transaction, new()
{
    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd,
        RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeULong();
        decoderContext.DecodeAddressStructRefNonNull(out AddressStructRef to);
        if (to != EezConstants.Eezl2Address)
        {
            throw new RlpException($"EEZ system transaction must target {EezConstants.Eezl2Address}.");
        }

        transaction.To = EezConstants.Eezl2Address;
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory(DataRlpLimit);
        transaction.SenderAddress = EezConstants.SystemAddress;
        transaction.GasLimit = EezConstants.SystemTxGasLimit;
        transaction.GasPrice = 0;
        transaction.DecodedMaxFeePerGas = 0;
        transaction.Signature = null;
    }

    protected override void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
        throw new RlpException("EEZ system transaction carries unexpected trailing fields.");

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.Encode(transaction.ChainId ?? 0);
        writer.Encode(transaction.Nonce);
        writer.Encode(transaction.To);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.Data);
    }

    protected override int GetPayloadLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + Rlp.LengthOf(transaction.Nonce)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.ValueRef)
        + Rlp.LengthOf(transaction.Data);

    protected override int GetSignatureLength(Signature? signature, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) => 0;

    protected override void EncodeSignature<TWriter>(Signature? signature, ref TWriter writer, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
    }
}
