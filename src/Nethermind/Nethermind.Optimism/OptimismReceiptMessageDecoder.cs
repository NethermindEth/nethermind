// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism;

[Rlp.Decoder(RlpDecoderKey.Trie)]
public sealed class OptimismReceiptTrieDecoder() : OptimismReceiptMessageDecoder(true);

[Rlp.Decoder]
public class OptimismReceiptMessageDecoder(bool isEncodedForTrie = false, bool skipStateAndStatus = false) : RlpDecoder<TxReceipt>
{
    private readonly bool _skipStateAndStatus = skipStateAndStatus;

    protected override OptimismTxReceipt? DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (ctx.IsNextItemEmptyList())
        {
            ctx.ReadByte();
            return null;
        }

        OptimismTxReceipt txReceipt = new();
        if (!ctx.IsSequenceNext())
        {
            ctx.SkipLength();
            txReceipt.TxType = (TxType)ctx.ReadByte();
        }

        int lastCheck = ctx.ReadSequenceLength() + ctx.Position;

        byte[] firstItem = ctx.DecodeByteArray();
        if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
        {
            txReceipt.StatusCode = firstItem[0];
            txReceipt.GasUsedTotal = ctx.DecodeULong();
        }
        else if (firstItem.Length is >= 1 and <= 4)
        {
            txReceipt.GasUsedTotal = firstItem.ToULong();
        }
        else
        {
            if (firstItem.Length != 0 && firstItem.Length != Hash256.Size)
            {
                ThrowUnexpectedPostStateLength(firstItem.Length);
            }

            txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            txReceipt.GasUsedTotal = ctx.DecodeULong();
        }

        txReceipt.Bloom = ctx.DecodeBloomNonNull();

        int logEntriesCheck = ctx.ReadSequenceLength() + ctx.Position;

        txReceipt.Logs = LogEntryDecoder.DecodeLogs(ref ctx, logEntriesCheck);

        if (lastCheck > ctx.Position)
        {
            if (txReceipt.TxType == TxType.DepositTx && lastCheck > ctx.Position)
            {
                txReceipt.DepositNonce = ctx.DecodeULong();

                if (lastCheck > ctx.Position)
                {
                    txReceipt.DepositReceiptVersion = ctx.DecodeULong();
                }
            }
        }

        return txReceipt;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedPostStateLength(int length)
            => throw new RlpException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");
    }

    /// <summary>Populates the receipt content into <paramref name="lengths"/>.</summary>
    private void PopulateContentLength(TxReceipt item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors)
    {
        LogEntryDecoder.Instance.PopulateSequenceLength(GetLogs(item), lengths);
        int contentLength = Rlp.LengthOf(item.GasUsedTotal);
        contentLength += Rlp.LengthOf(item.Bloom);

        bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

        if (!_skipStateAndStatus)
        {
            contentLength += isEip658Receipts
                ? Rlp.LengthOf(item.StatusCode)
                : Rlp.LengthOf(item.PostTransactionState);
        }

        if (item.IsOptimismTxReceipt(out OptimismTxReceipt? opItem))
        {
            if (opItem.DepositNonce is not null && (opItem.DepositReceiptVersion is not null || !isEncodedForTrie))
            {
                contentLength += Rlp.LengthOf(opItem.DepositNonce);

                if (opItem.DepositReceiptVersion is not null)
                {
                    contentLength += Rlp.LengthOf(opItem.DepositReceiptVersion.Value);
                }
            }
        }

        lengths.Add(contentLength);
    }

    private static LogEntry[] GetLogs(TxReceipt item)
        => item.Logs ?? throw new RlpException("Receipt logs are null.");

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2718
    /// </summary>
    public override void PopulateLength(TxReceipt? item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Add(Rlp.LengthOfSequence(0));
            return;
        }

        LengthListWriter sequence = lengths.BeginContainer();
        PopulateContentLength(item, sequence, rlpBehaviors);
        int receiptPayloadLength = Rlp.LengthOfSequence(sequence.Length);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        lengths.Add(item.TxType != TxType.Legacy
            ? isForTxRoot
                ? (1 + receiptPayloadLength)
                : Rlp.LengthOfSequence(1 + receiptPayloadLength) // Rlp(TransactionType || TransactionPayload)
            : receiptPayloadLength);
    }

    public override void Encode<TWriter>(ref TWriter writer, TxReceipt? item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        int totalContentLength = lengths.Read();
        int sequenceLength = Rlp.LengthOfSequence(totalContentLength);

        if (item.TxType != TxType.Legacy)
        {
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
            {
                writer.StartByteArray(sequenceLength + 1, false);
            }

            writer.WriteByte((byte)item.TxType);
        }

        writer.StartSequence(totalContentLength);
        if (!_skipStateAndStatus)
        {
            writer.Encode(item.StatusCode);
        }

        writer.Encode(item.GasUsedTotal);
        writer.Encode(item.Bloom);

        LogEntryDecoder.Instance.EncodeSequence(ref writer, GetLogs(item), ref lengths);

        if (item.IsOptimismTxReceipt(out OptimismTxReceipt? opItem))
        {
            if (opItem.DepositNonce is not null && (opItem.DepositReceiptVersion is not null || !isEncodedForTrie))
            {
                writer.Encode(opItem.DepositNonce.Value);

                if (opItem.DepositReceiptVersion is not null)
                {
                    writer.Encode(opItem.DepositReceiptVersion.Value);
                }
            }
        }
    }
}

internal static class TxReceiptExt
{
    internal static bool IsOptimismTxReceipt(this TxReceipt item, [NotNullWhen(true)] out OptimismTxReceipt? opItem)
    {
        opItem = null;

        if (item.TxType != TxType.DepositTx)
        {
            return false;
        }

        if (item is not OptimismTxReceipt casted)
        {
            throw new InvalidCastException($"{nameof(TxReceipt)} of type {item.TxType} is not an instance of {nameof(OptimismTxReceipt)}");
        }

        opItem = casted;
        return true;
    }
}
