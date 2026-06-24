// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Optimism;

public sealed class OptimismTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.DepositTx, transactionFactory) where T : Transaction, new()
{
    protected override int GetSignatureLength(Signature? signature, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) => 0;

    protected override void EncodeSignature<TWriter>(Signature? signature, ref TWriter writer, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
    }

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.SourceHash = decoderContext.DecodeKeccak();
        transaction.SenderAddress = decoderContext.DecodeAddress();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Mint = decoderContext.DecodeUInt256();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodePositiveLong();
        transaction.IsOPSystemTransaction = decoderContext.DecodeBool();
        transaction.Data = decoderContext.DecodeByteArray();
    }

    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? fallbackSignature
            : base.DecodeSignature(v, rBytes, sBytes, fallbackSignature, rlpBehaviors);

    protected override int GetPayloadLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.SourceHash)
        + Rlp.LengthOf(transaction.SenderAddress)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.Mint)
        + Rlp.LengthOf(in transaction.ValueRef)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.IsOPSystemTransaction)
        + Rlp.LengthOf(transaction.Data);

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.Encode(transaction.SourceHash);
        writer.Encode(transaction.SenderAddress);
        writer.Encode(transaction.To);
        writer.Encode(transaction.Mint);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.IsOPSystemTransaction);
        writer.Encode(transaction.Data);
    }
}
