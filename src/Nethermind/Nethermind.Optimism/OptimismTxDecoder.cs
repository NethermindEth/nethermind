// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Optimism;

public sealed class OptimismTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(TxType.DepositTx, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);

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

    private static int GetContentLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.SourceHash)
        + Rlp.LengthOf(transaction.SenderAddress)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.Mint)
        + Rlp.LengthOf(transaction.ValueRef)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.IsOPSystemTransaction)
        + Rlp.LengthOf(transaction.Data);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors)
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

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
        {
            ulong v = DecodeSignatureItems(ref decoderContext, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
            if (v != 0 || !rBytes.IsEmpty || !sBytes.IsEmpty)
            {
                transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
            }
        }
    }
}
