// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EraE.Archive;

internal sealed class EraSlimReceiptDecoder
{
    public TxReceipt[] Decode(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);

        int outerLength = ctx.ReadSequenceLength();
        int outerEnd = ctx.Position + outerLength;
        int count = ctx.PeekNumberOfItemsRemaining(outerEnd);

        TxReceipt[] receipts = new TxReceipt[count];
        for (int i = 0; i < count; i++)
        {
            receipts[i] = DecodeSlimReceipt(ref ctx);
        }

        if (ctx.Position != outerEnd)
            throw new RlpException("Receipt list was not fully consumed.");

        return receipts;
    }

    private static TxReceipt DecodeSlimReceipt(ref Rlp.ValueDecoderContext ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int receiptEnd = ctx.Position + sequenceLength;

        TxReceipt receipt = new();

        byte[] txTypeBytes = ctx.DecodeByteArray();
        if (txTypeBytes.Length > 1)
            throw new RlpException($"Invalid slim receipt tx_type length: {txTypeBytes.Length}");

        receipt.TxType = txTypeBytes.Length == 0
            ? TxType.Legacy
            : (TxType)txTypeBytes[0];

        byte[] statusBytes = ctx.DecodeByteArray();
        switch (statusBytes.Length)
        {
            case 0:
                receipt.StatusCode = 0;
                break;

            case 1:
                receipt.StatusCode = statusBytes[0];
                break;

            case Keccak.Size:
                receipt.PostTransactionState = new Hash256(statusBytes);
                break;

            default:
                throw new RlpException(
                    $"Invalid slim receipt status encoding length: {statusBytes.Length}");
        }

        receipt.GasUsedTotal = ctx.DecodePositiveLong();

        int logsLength = ctx.ReadSequenceLength();
        int logsEnd = ctx.Position + logsLength;
        int logCount = ctx.PeekNumberOfItemsRemaining(logsEnd);

        LogEntry[] logs = new LogEntry[logCount];
        for (int i = 0; i < logCount; i++)
        {
            logs[i] = Rlp.Decode<LogEntry>(ref ctx);
        }

        if (ctx.Position != logsEnd)
            throw new RlpException("Slim receipt logs list was not fully consumed.");

        receipt.Logs = logs;

        if (ctx.Position != receiptEnd)
            throw new RlpException("Slim receipt sequence was not fully consumed.");

        return receipt;
    }
}
