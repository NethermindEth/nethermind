// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Optimism;

public sealed class OptimismTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(TxType.DepositTx, transactionFactory)
{
    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction);
        StartTypedTransaction(ref writer, TxType.DepositTx, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(transaction.SourceHash);
        writer.Encode(transaction.SenderAddress);
        writer.Encode(transaction.To);
        writer.Encode(transaction.Mint);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.IsOPSystemTransaction);
        writer.Encode(transaction.Data);
        writer.EndSequence(sequence);
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
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag
        {
            transaction.SourceHash = reader.DecodeKeccak();
            transaction.SenderAddress = reader.DecodeAddress();
            transaction.To = reader.DecodeAddressOrNull();
            transaction.Mint = reader.DecodeUInt256<TCanonicalChecks>();
            transaction.Value = reader.DecodeUInt256<TCanonicalChecks>();
            transaction.GasLimit = reader.DecodeULong<TCanonicalChecks>();
            transaction.IsOPSystemTransaction = reader.DecodeBool();
            transaction.Data = reader.DecodeByteArray<TCanonicalChecks>();
        }

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag
        {
            ulong v = DecodeSignatureItems<TCanonicalChecks>(ref reader, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
            if (v != 0 || !rBytes.IsEmpty || !sBytes.IsEmpty)
            {
                transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
            }
        }
    }
}
