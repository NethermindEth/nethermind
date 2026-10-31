// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EraE.Archive;

internal sealed class EraSlimReceiptDecoder
{
    /// <summary>Smallest RLP encoding of a slim receipt.</summary>
    /// <remarks><c>["", "", "", []]</c> - four one-byte items under a one-byte sequence prefix.</remarks>
    private const int MinSlimReceiptLength = 5;

    private static readonly RlpLimit ReceiptsRlpLimit = RlpLimit.For<TxReceipt>(RlpLimit.DefaultLimit.Limit);

    public TxReceipt[] Decode(Memory<byte> buffer)
    {
        RlpReader ctx = new RlpReader(buffer.Span).EnterSequence(out _);

        // The entry is decompressed without a size cap, so the count must be bounded by its own bytes.
        int maxReceipts = Math.Min(ReceiptsRlpLimit.Limit, ctx.Remaining / MinSlimReceiptLength);
        int count = ctx.CountItems(maxReceipts);

        TxReceipt[] receipts = new TxReceipt[count];
        for (int i = 0; i < count; i++)
        {
            receipts[i] = DecodeSlimReceipt(ref ctx);
        }

        if (ctx.HasNext)
            throw new RlpReaderException("Receipt list was not fully consumed.");

        return receipts;
    }

    private static TxReceipt DecodeSlimReceipt(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int receiptLength);

        TxReceipt receipt = new();

        byte[] txTypeBytes = ctx.DecodeByteArray();
        if (txTypeBytes.Length > 1)
            throw new RlpReaderException($"Invalid slim receipt tx_type length: {txTypeBytes.Length}");

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
                throw new RlpReaderException(
                    $"Invalid slim receipt status encoding length: {statusBytes.Length}");
        }

        receipt.GasUsedTotal = ctx.DecodeULong();

        RlpReader logsReader = ctx.EnterSequence(out int logsLength);
        LogEntry[] logs = LogEntryDecoder.DecodeLogs(ref logsReader);

        if (logsReader.HasNext)
            throw new RlpReaderException("Slim receipt logs list was not fully consumed.");

        ctx.ExitSequence(logsReader, logsLength);
        receipt.Logs = logs;

        if (ctx.HasNext)
            throw new RlpReaderException("Slim receipt sequence was not fully consumed.");

        reader.ExitSequence(ctx, receiptLength);
        return receipt;
    }
}
