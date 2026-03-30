// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EraE.Archive;

internal sealed class EraSlimReceiptDecoder
{
    private readonly ReceiptMessageDecoder _inner = new(skipBloom: true);

    public TxReceipt[] Decode(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);

        int outerLength = ctx.ReadSequenceLength();
        int outerEnd = ctx.Position + outerLength;
        int count = ctx.PeekNumberOfItemsRemaining(outerEnd);

        TxReceipt[] receipts = new TxReceipt[count];
        for (int i = 0; i < count; i++)
        {
            receipts[i] = DecodeOne(ref ctx);
        }

        return receipts;
    }

    private TxReceipt DecodeOne(ref Rlp.ValueDecoderContext ctx)
    {
        // Nethermind typed receipt: encoded as an RLP byte-array (not a sequence)
        if (!ctx.IsSequenceNext())
            return _inner.Decode(ref ctx);

        int savedPosition = ctx.Position;
        int sequenceLength = ctx.ReadSequenceLength();
        int receiptEnd = ctx.Position + sequenceLength;
        int fieldCount = ctx.PeekNumberOfItemsRemaining(receiptEnd);
        ctx.Position = savedPosition;

        if (fieldCount != 4)
        {
            // Nethermind 3-field format: delegate to existing slim decoder
            return _inner.Decode(ref ctx);
        }

        // go-ethereum 4-field format: [tx_type, status, cumulative_gas, logs]
        ctx.ReadSequenceLength(); // consume the sequence header (matches the peek above)

        TxReceipt receipt = new();

        byte[] txTypeBytes = ctx.DecodeByteArray();
        receipt.TxType = txTypeBytes.Length == 0 ? TxType.Legacy : (TxType)txTypeBytes[0];

        byte[] statusBytes = ctx.DecodeByteArray();
        receipt.StatusCode = statusBytes.Length == 0 ? (byte)0 : statusBytes[0];

        receipt.GasUsedTotal = ctx.DecodePositiveLong();

        int logsEnd = ctx.ReadSequenceLength() + ctx.Position;
        int logCount = ctx.PeekNumberOfItemsRemaining(logsEnd);
        LogEntry[] logs = new LogEntry[logCount];
        for (int i = 0; i < logCount; i++)
        {
            logs[i] = Rlp.Decode<LogEntry>(ref ctx, RlpBehaviors.AllowExtraBytes);
        }
        receipt.Logs = logs;

        ctx.Position = receiptEnd;
        return receipt;
    }
}
