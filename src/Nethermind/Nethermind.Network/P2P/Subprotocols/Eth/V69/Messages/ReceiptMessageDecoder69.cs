// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

[Rlp.SkipGlobalRegistration] // Created explicitly
public sealed class ReceiptMessageDecoder69(bool skipStateAndStatus = false) : RlpDecoder<TxReceipt>
{
    protected override TxReceipt? DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (ctx.IsNextItemEmptyList())
        {
            ctx.ReadByte();
            return null;
        }

        TxReceipt txReceipt = new();

        int sequenceLength = ctx.ReadSequenceLength();
        int receiptEnd = ctx.Position + sequenceLength;

        txReceipt.TxType = (TxType)ctx.DecodeByte();

        if (txReceipt.TxType == TxType.FrameTx)
        {
            FrameReceiptRlp.DecodePayload(ref ctx, txReceipt, receiptEnd, rlpBehaviors);
            return txReceipt;
        }

        ReadOnlySpan<byte> firstItem = ctx.DecodeByteArraySpan();
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

        int lastCheck = ctx.ReadSequenceLength() + ctx.Position;

        txReceipt.Logs = LogEntryDecoder.DecodeLogs(ref ctx, lastCheck);

        // The item count only requires a log to start before the declared end, so an under-declared
        // logs header is only caught here; the logs are last, so the receipt end lands on it.
        ctx.Check(lastCheck);

        // Handle any remaining extra bytes
        bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
        if (ctx.Position != receiptEnd)
        {
            if (allowExtraBytes)
            {
                ctx.Position = receiptEnd;
            }
            else
            {
                ThrowUnexpectedReceiptField();
            }
        }

        return txReceipt;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedPostStateLength(int length)
            => throw new RlpException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedReceiptField()
            => throw new RlpException("Unexpected receipt field");
    }

    /// <summary>Populates the receipt and returns its content length.</summary>
    private int PopulateContentLength(TxReceipt item, ref LengthStack lengths, RlpBehaviors rlpBehaviors)
    {
        if (item.TxType == TxType.FrameTx)
        {
            return Rlp.LengthOf((byte)item.TxType) + FrameReceiptRlp.PopulatePayloadLength(item, ref lengths);
        }

        int contentLength = LogEntryDecoder.Instance.PopulateSequenceAndPeek(GetLogs(item), ref lengths);
        contentLength += Rlp.LengthOf((byte)item.TxType);
        contentLength += Rlp.LengthOf(item.GasUsedTotal);

        bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

        if (!skipStateAndStatus)
        {
            contentLength += isEip658Receipts
                ? Rlp.LengthOf(item.StatusCode)
                : Rlp.LengthOf(item.PostTransactionState);
        }

        return contentLength;
    }

    private static LogEntry[] GetLogs(TxReceipt item) =>
        item.Logs ?? throw new RlpException("Receipt logs are null.");

    public override void PopulateLength(TxReceipt? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Push(Rlp.OfEmptyList.Length);
            return;
        }

        int contentLength = PopulateContentLength(item, ref lengths, rlpBehaviors);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    public override void Encode<TWriter>(TxReceipt? item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        lengths.Pop();
        if (item is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        writer.StartSequence(lengths.Pop());

        writer.Encode((byte)item.TxType);

        if (item.TxType == TxType.FrameTx)
        {
            FrameReceiptRlp.EncodePayload(ref writer, item, ref lengths);
            return;
        }

        if (!skipStateAndStatus)
        {
            if ((rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts)
            {
                writer.Encode(item.StatusCode);
            }
            else
            {
                writer.Encode(item.PostTransactionState);
            }
        }

        writer.Encode(item.GasUsedTotal);

        LogEntryDecoder.Instance.EncodeSequence(GetLogs(item), ref lengths, ref writer);
    }
}
