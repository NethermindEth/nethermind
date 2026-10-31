// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using static Nethermind.Serialization.Rlp.Rlp;

namespace Nethermind.Optimism;

[Decoder(RlpDecoderKey.Storage)]
public class OptimismCompactReceiptStorageDecoder :
    RlpDecoder<TxReceipt>, IReceiptRefDecoder
{
    private static readonly CompactLogEntryDecoder LogEntryDecoder = CompactLogEntryDecoder.Instance;

    protected override OptimismTxReceipt? DecodeInternal(ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<OffFlag>(ref decoderContext, rlpBehaviors)
            : Decode<OnFlag>(ref decoderContext, rlpBehaviors);

    private static OptimismTxReceipt? Decode<TCanonicalChecks>(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }

        OptimismTxReceipt txReceipt = new();
        RlpReader fields = decoderContext.EnterSequence<TCanonicalChecks>(out int receiptLength);

        ReadOnlySpan<byte> firstItem = fields.ReadBytes<TCanonicalChecks>();
        if (firstItem.Length == 1)
        {
            txReceipt.StatusCode = firstItem[0];
        }
        else
        {
            if (firstItem.Length != 0 && firstItem.Length != Hash256.Size)
            {
                ThrowUnexpectedPostStateLength(firstItem.Length);
            }

            txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
        }

        txReceipt.Sender = fields.DecodeAddressOrNull();
        txReceipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();

        RlpReader logs = fields.EnterSequence<TCanonicalChecks>(out int logsLength);

        // Don't know the size exactly, I'll just assume its just an address and add some margin
        using ArrayPoolListRef<LogEntry> logEntries = new(logs.Remaining * 2 / LengthOfAddressRlp);
        while (logs.HasNext)
        {
            LogEntry? logEntry = LogEntryDecoder.Decode(ref logs, rlpBehaviors);
            if (logEntry is not null)
            {
                logEntries.Add(logEntry);
            }
        }

        fields.ExitSequence(in logs, logsLength);
        txReceipt.Logs = [.. logEntries];

        if (fields.HasNext)
        {
            txReceipt.DepositNonce = fields.DecodeULong<TCanonicalChecks>();
        }

        if (fields.HasNext)
        {
            txReceipt.DepositReceiptVersion = fields.DecodeULong<TCanonicalChecks>();
        }

        decoderContext.ExitSequence(in fields, receiptLength, rlpBehaviors);
        return txReceipt;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedPostStateLength(int length)
            => throw new RlpReaderException($"Unexpected post-transaction state length {length}; expected 0 or {Hash256.Size} bytes");
    }

    public void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors, out TxReceiptStructRef item)
    {
        if ((rlpBehaviors & RlpBehaviors.TrustedInput) != 0)
        {
            DecodeStructRef<OffFlag>(ref decoderContext, out item);
        }
        else
        {
            DecodeStructRef<OnFlag>(ref decoderContext, out item);
        }
    }

    private static void DecodeStructRef<TCanonicalChecks>(scoped ref RlpReader decoderContext, out TxReceiptStructRef item)
        where TCanonicalChecks : struct, IFlag
    {
        // Note: This method runs at 2.5 million times/sec on my machine
        item = new TxReceiptStructRef();

        if (decoderContext.TryReadEmptySequence())
        {
            return;
        }

        RlpReader fields = decoderContext.EnterSequence<TCanonicalChecks>(out int receiptLength);

        ReadOnlySpan<byte> firstItem = fields.DecodeByteArraySpan<TCanonicalChecks>();
        if (firstItem.Length == 1)
        {
            item.StatusCode = firstItem[0];
        }
        else
        {
            item.PostTransactionState =
                firstItem.Length == 0 ? new Hash256StructRef() : new Hash256StructRef(firstItem);
        }

        fields.DecodeAddressStructRef(out item.Sender);
        item.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
        item.LogsRlp = fields.ReadEncodedItem<TCanonicalChecks>();

        // The deposit nonce and the deposit receipt version.
        if (fields.HasNext)
        {
            fields.SkipItem<TCanonicalChecks>();
        }

        if (fields.HasNext)
        {
            fields.SkipItem<TCanonicalChecks>();
        }

        decoderContext.ExitSequence(in fields, receiptLength);
    }

    public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors none,
        out LogEntryStructRef current) => CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);

    public Hash256[] DecodeTopics(RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => CompactLogEntryDecoder.DecodeTopics(reader, rlpBehaviors);

    // Refstruct decode does not generate bloom
    public bool CanDecodeBloom => false;

    public override void Encode(ref RlpWriter writer, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

        bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

        // Note: Any byte saved here is about 3GB on mainnet.
        RlpSequence sequence = writer.StartSequence(totalContentLength);
        if (isEip658receipts)
        {
            writer.Encode(item.StatusCode);
        }
        else
        {
            writer.Encode(item.PostTransactionState);
        }

        writer.Encode(item.Sender);
        writer.Encode(item.GasUsedTotal);

        RlpSequence logsSequence = writer.StartSequence(logsLength);

        LogEntry[] logs = GetLogs(item);
        for (int i = 0; i < logs.Length; i++)
        {
            LogEntryDecoder.Encode(ref writer, logs[i]);
        }

        writer.EndSequence(logsSequence);

        if (item.IsOptimismTxReceipt(out OptimismTxReceipt? opItem) && opItem.DepositNonce is not null)
        {
            writer.Encode(opItem.DepositNonce.Value);

            if (opItem.DepositReceiptVersion is not null)
            {
                writer.Encode(opItem.DepositReceiptVersion.Value);
            }
        }

        writer.EndSequence(sequence);
    }

    private static (int Total, int Logs) GetContentLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;
        if (item is null)
        {
            return (contentLength, 0);
        }

        bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;
        if (isEip658Receipts)
        {
            contentLength += LengthOf(item.StatusCode);
        }
        else
        {
            contentLength += LengthOf(item.PostTransactionState);
        }

        contentLength += LengthOf(item.Sender);
        contentLength += LengthOf(item.GasUsedTotal);

        int logsLength = GetLogsLength(item);
        contentLength += LengthOfSequence(logsLength);

        if (item.IsOptimismTxReceipt(out OptimismTxReceipt? opItem) && opItem.DepositNonce is not null)
        {
            contentLength += LengthOf(opItem.DepositNonce);

            if (opItem.DepositReceiptVersion is not null)
            {
                contentLength += LengthOf(opItem.DepositReceiptVersion.Value);
            }
        }

        return (contentLength, logsLength);
    }

    private static int GetLogsLength(TxReceipt item)
    {
        int logsLength = 0;
        LogEntry[] logs = GetLogs(item);
        for (int i = 0; i < logs.Length; i++)
        {
            logsLength += LogEntryDecoder.GetLength(logs[i]);
        }

        return logsLength;
    }

    private static LogEntry[] GetLogs(TxReceipt item)
        => item.Logs ?? throw new RlpReaderException("Receipt logs are null.");

    public override int GetLength(TxReceipt? item, RlpBehaviors rlpBehaviors)
    {
        (int Total, _) = GetContentLength(item, rlpBehaviors);
        return LengthOfSequence(Total);
    }
}
