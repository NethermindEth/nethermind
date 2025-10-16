// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

[Rlp.SkipGlobalRegistration] // Created explicitly
public class ReceiptMessageDecoder69(bool skipStateAndStatus = false) : IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>
{
    public TxReceipt? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        TxReceipt response = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return response;
    }

    public TxReceipt? Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (ctx.IsNextItemNull())
        {
            ctx.ReadByte();
            return null;
        }

        TxReceipt txReceipt = new();

        _ = ctx.ReadSequenceLength();

        txReceipt.TxType = (TxType)ctx.DecodeByte();

        byte[] firstItem = ctx.DecodeByteArray();
        if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
        {
            txReceipt.StatusCode = firstItem[0];
            txReceipt.GasUsedTotal = (long)ctx.DecodeUBigInt();
        }
        else if (firstItem.Length is >= 1 and <= 4)
        {
            txReceipt.GasUsedTotal = (long)firstItem.ToUnsignedBigInteger();
        }
        else
        {
            txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
            txReceipt.GasUsedTotal = (long)ctx.DecodeUBigInt();
        }

        int lastCheck = ctx.ReadSequenceLength() + ctx.Position;

        int numberOfReceipts = ctx.PeekNumberOfItemsRemaining(lastCheck);
        ctx.GuardLimit(numberOfReceipts);
        LogEntry[] entries = new LogEntry[numberOfReceipts];
        for (int i = 0; i < numberOfReceipts; i++)
        {
            entries[i] = Rlp.Decode<LogEntry>(ref ctx, RlpBehaviors.AllowExtraBytes);
        }

        txReceipt.Logs = entries;

        return txReceipt;
    }

    private (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
        {
            return (0, 0);
        }

        int contentLength = 0;
        contentLength += Rlp.LengthOf((byte)item.TxType);
        contentLength += Rlp.LengthOf(item.GasUsedTotal);

        int logsLength = GetLogsLength(item);
        contentLength += Rlp.LengthOfSequence(logsLength);

        bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

        if (!skipStateAndStatus)
        {
            contentLength += isEip658Receipts
                ? Rlp.LengthOf(item.StatusCode)
                : Rlp.LengthOf(item.PostTransactionState);
        }

        return (contentLength, logsLength);
    }

    private static int GetLogsLength(TxReceipt item)
    {
        int logsLength = 0;
        for (var i = 0; i < item.Logs.Length; i++)
        {
            logsLength += Rlp.LengthOf(item.Logs[i]);
        }

        return logsLength;
    }

    public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
    {
        (int total, _) = GetContentLength(item, rlpBehaviors);
        return Rlp.LengthOfSequence(total);
    }

    public void Encode(RlpStream rlpStream, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            rlpStream.EncodeNullObject();
            return;
        }

        (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

        rlpStream.StartSequence(totalContentLength);

        rlpStream.Encode((byte)item.TxType);

        if (!skipStateAndStatus)
        {
            if ((rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts)
            {
                rlpStream.Encode(item.StatusCode);
            }
            else
            {
                rlpStream.Encode(item.PostTransactionState);
            }
        }

        rlpStream.Encode(item.GasUsedTotal);

        rlpStream.StartSequence(logsLength);
        LogEntry[] logs = item.Logs;
        for (var i = 0; i < logs.Length; i++)
        {
            rlpStream.Encode(logs[i]);
        }
    }
}
