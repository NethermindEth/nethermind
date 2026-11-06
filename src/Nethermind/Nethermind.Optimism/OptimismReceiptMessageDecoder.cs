// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism;

[Rlp.Decoder(RlpDecoderKey.Trie)]
public sealed class OptimismReceiptTrieDecoder() : OptimismReceiptMessageDecoder(true);

[Rlp.Decoder]
public class OptimismReceiptMessageDecoder(bool isEncodedForTrie = false, bool skipStateAndStatus = false) : RlpDecoder<TxReceipt>
{
    private readonly bool _skipStateAndStatus = skipStateAndStatus;

    protected override OptimismTxReceipt? DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<OffFlag>(ref ctx, rlpBehaviors)
            : Decode<OnFlag>(ref ctx, rlpBehaviors);

    private static OptimismTxReceipt? Decode<TCanonicalChecks>(ref RlpReader ctx, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        if (ctx.TryReadEmptySequence())
        {
            return null;
        }

        OptimismTxReceipt txReceipt = new();
        byte first = ctx.PeekByte();
        if (first >= Rlp.EmptyListByte)
        {
            DecodeReceipt<TCanonicalChecks>(ref ctx, txReceipt, rlpBehaviors);
        }
        else if (first < Rlp.EmptyByteArrayByte)
        {
            // The type byte without the byte string around it, as the receipt trie holds it.
            ctx.ReadRaw(1);
            txReceipt.TxType = (TxType)first;
            DecodeReceipt<TCanonicalChecks>(ref ctx, txReceipt, rlpBehaviors);
        }
        else
        {
            ReadOnlySpan<byte> envelope = ctx.ReadBytes<TCanonicalChecks>();
            if (envelope.IsEmpty) ThrowEmptyEnvelope();

            txReceipt.TxType = (TxType)envelope[0];
            RlpReader payload = new(envelope[1..]);
            DecodeReceipt<TCanonicalChecks>(ref payload, txReceipt, rlpBehaviors);
        }

        return txReceipt;
    }

    private static void DecodeReceipt<TCanonicalChecks>(ref RlpReader ctx, OptimismTxReceipt txReceipt, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = ctx.EnterSequence<TCanonicalChecks>(out int receiptLength);

        ReadOnlySpan<byte> firstItem = fields.ReadBytes<TCanonicalChecks>();
        if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
        {
            txReceipt.StatusCode = firstItem[0];
            txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
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
            txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
        }

        txReceipt.Bloom = fields.DecodeBloomNonNull();

        RlpReader logs = fields.EnterSequence<TCanonicalChecks>(out int logsLength);
        txReceipt.Logs = LogEntryDecoder.DecodeLogs(ref logs, rlpBehaviors);
        fields.ExitSequence(in logs, logsLength);

        if (txReceipt.TxType == TxType.DepositTx && fields.HasNext)
        {
            txReceipt.DepositNonce = fields.DecodeULong<TCanonicalChecks>();

            if (fields.HasNext)
            {
                txReceipt.DepositReceiptVersion = fields.DecodeULong<TCanonicalChecks>();
            }
        }

        ctx.ExitSequenceIgnoringRest(in fields, receiptLength);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedPostStateLength(int length)
            => throw new RlpReaderException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyEnvelope()
        => throw new RlpReaderException("Typed receipt envelope is empty");

    private (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
        {
            return (0, 0);
        }

        int contentLength = 0;
        contentLength += Rlp.LengthOf(item.GasUsedTotal);
        contentLength += Rlp.LengthOf(item.Bloom);

        int logsLength = GetLogsLength(item);
        contentLength += Rlp.LengthOfSequence(logsLength);

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

        return (contentLength, logsLength);
    }

    public static int GetLogsLength(TxReceipt item)
    {
        int logsLength = 0;
        LogEntry[] logs = GetLogs(item);
        for (int i = 0; i < logs.Length; i++)
        {
            logsLength += Rlp.LengthOf(logs[i]);
        }

        return logsLength;
    }

    private static LogEntry[] GetLogs(TxReceipt item)
        => item.Logs ?? throw new RlpReaderException("Receipt logs are null.");

    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2718
    /// </summary>
    public override int GetLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
        {
            return Rlp.LengthOfSequence(0);
        }

        (int total, _) = GetContentLength(item, rlpBehaviors);
        int receiptPayloadLength = Rlp.LengthOfSequence(total);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = item.TxType != TxType.Legacy
            ? isForTxRoot
                ? (1 + receiptPayloadLength)
                : Rlp.LengthOfSequence(1 + receiptPayloadLength) // Rlp(TransactionType || TransactionPayload)
            : receiptPayloadLength;
        return result;
    }

    public override void Encode(ref RlpWriter writer, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);
        int sequenceLength = Rlp.LengthOfSequence(totalContentLength);

        if (item.TxType != TxType.Legacy)
        {
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
            {
                writer.StartByteArray(sequenceLength + 1, false);
            }

            writer.WriteByte((byte)item.TxType);
        }

        RlpSequence sequence = writer.StartSequence(totalContentLength);
        if (!_skipStateAndStatus)
        {
            writer.Encode(item.StatusCode);
        }

        writer.Encode(item.GasUsedTotal);
        writer.Encode(item.Bloom);

        RlpSequence logsSequence = writer.StartSequence(logsLength);
        LogEntry[] logs = GetLogs(item);
        for (int i = 0; i < logs.Length; i++)
        {
            LogEntryDecoder.Instance.Encode(ref writer, logs[i]);
        }

        writer.EndSequence(logsSequence);

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

        writer.EndSequence(sequence);
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
