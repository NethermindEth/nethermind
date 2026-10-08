// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Optimism;

public sealed class OptimismTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.DepositTx, transactionFactory)
{
    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction);
        StartTypedTransaction(ref writer, TxType.DepositTx, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        writer.StartSequence(contentLength);
        writer.Encode(transaction.SourceHash);
        writer.Encode(transaction.SenderAddress);
        writer.Encode(transaction.To);
        writer.Encode(transaction.Mint);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.IsOPSystemTransaction);
        writer.Encode(transaction.Data);
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction)), rlpBehaviors);

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd,
        RlpBehaviors rlpBehaviors)
    {
        transaction.SourceHash = decoderContext.DecodeKeccak();
        transaction.SenderAddress = decoderContext.DecodeAddress();
        transaction.To = decoderContext.DecodeAddressOrNull();
        transaction.Mint = decoderContext.DecodeUInt256();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeULong();
        transaction.IsOPSystemTransaction = decoderContext.DecodeBool();
        transaction.Data = decoderContext.DecodeByteArray();
    }

    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        v == 0 && rBytes.IsEmpty && sBytes.IsEmpty
            ? fallbackSignature
            : base.DecodeSignature(v, rBytes, sBytes, fallbackSignature, rlpBehaviors);

    private static int GetContentLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.SourceHash)
        + Rlp.LengthOf(transaction.SenderAddress)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.Mint)
        + Rlp.LengthOf(transaction.ValueRef)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.IsOPSystemTransaction)
        + Rlp.LengthOf(transaction.Data);
}
