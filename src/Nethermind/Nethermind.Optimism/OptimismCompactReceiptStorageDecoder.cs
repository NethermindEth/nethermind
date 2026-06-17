// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using static Nethermind.Serialization.Rlp.Rlp;

namespace Nethermind.Optimism;

[Decoder(RlpDecoderKey.Storage)]
public class OptimismCompactReceiptStorageDecoder :
    RlpDecoder<TxReceipt>, IReceiptRefDecoder
{
    private static readonly CompactLogEntryDecoder LogEntryDecoder = CompactLogEntryDecoder.Instance;

    protected override OptimismTxReceipt DecodeInternal(ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null!;
        }

        OptimismTxReceipt txReceipt = new();
        int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;

        byte[] firstItem = decoderContext.DecodeByteArray();
        if (firstItem.Length == 1)
        {
            txReceipt.StatusCode = firstItem[0];
        }
        else
        {
            txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
        }

        txReceipt.Sender = decoderContext.DecodeAddress();
        txReceipt.GasUsedTotal = decoderContext.DecodePositiveLong();

        int sequenceLength = decoderContext.ReadSequenceLength();
        int logEntriesCheck = sequenceLength + decoderContext.Position;

        // Don't know the size exactly, I'll just assume its just an address and add some margin
        using ArrayPoolListRef<LogEntry> logEntries = new(sequenceLength * 2 / LengthOfAddressRlp);
        while (decoderContext.Position < logEntriesCheck)
        {
            logEntries.Add(LogEntryDecoder.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes)!);
        }

        txReceipt.Logs = [.. logEntries];

        if (lastCheck > decoderContext.Position)
        {
            int remainingItems = decoderContext.PeekNumberOfItemsRemaining(lastCheck);
            if (remainingItems > 0)
            {
                txReceipt.DepositNonce = decoderContext.DecodeULong();
            }

            if (remainingItems > 1)
            {
                txReceipt.DepositReceiptVersion = decoderContext.DecodeULong();
            }
        }

        bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
        if (!allowExtraBytes)
        {
            decoderContext.Check(lastCheck);
        }

        txReceipt.Bloom = new Bloom(txReceipt.Logs);

        return txReceipt;
    }

    public void DecodeStructRef(scoped ref RlpReader decoderContext, RlpBehaviors rlpBehaviors, out TxReceiptStructRef item)
    {
        // Note: This method runs at 2.5 million times/sec on my machine
        item = new TxReceiptStructRef();

        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return;
        }

        int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;

        ReadOnlySpan<byte> firstItem = decoderContext.DecodeByteArraySpan();
        if (firstItem.Length == 1)
        {
            item.StatusCode = firstItem[0];
        }
        else
        {
            item.PostTransactionState =
                firstItem.Length == 0 ? new Hash256StructRef() : new Hash256StructRef(firstItem);
        }

        decoderContext.DecodeAddressStructRef(out item.Sender);
        item.GasUsedTotal = decoderContext.DecodePositiveLong();

        (int prefixLength, int contentLength) = decoderContext.PeekPrefixAndContentLength();
        int logsBytes = contentLength + prefixLength;
        item.LogsRlp = decoderContext.Data.Slice(decoderContext.Position, logsBytes);

        if (lastCheck > decoderContext.Position)
        {
            int remainingItems = decoderContext.PeekNumberOfItemsRemaining(lastCheck);

            if (remainingItems > 1)
            {
                decoderContext.SkipItem();
            }

            if (remainingItems > 2)
            {
                decoderContext.SkipItem();
            }
        }

        decoderContext.SkipItem();
    }

    public void DecodeLogEntryStructRef(scoped ref RlpReader decoderContext, RlpBehaviors none,
        out LogEntryStructRef current) => CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);

    public Hash256[] DecodeTopics(RlpReader reader) => CompactLogEntryDecoder.DecodeTopics(reader);

    // Refstruct decode does not generate bloom
    public bool CanDecodeBloom => false;

    public override void Encode<TWriter>(ref TWriter writer, TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

        bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

        // Note: Any byte saved here is about 3GB on mainnet.
        writer.StartSequence(totalContentLength);
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

        writer.StartSequence(logsLength);

        LogEntry[] logs = item.Logs ?? [];
        for (int i = 0; i < logs.Length; i++)
        {
            LogEntryDecoder.Encode(ref writer, logs[i]);
        }

        if (item.IsOptimismTxReceipt(out OptimismTxReceipt? opItem) && opItem.DepositNonce is not null)
        {
            writer.Encode(opItem.DepositNonce.Value);

            if (opItem.DepositReceiptVersion is not null)
            {
                writer.Encode(opItem.DepositReceiptVersion.Value);
            }
        }
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
        LogEntry[] logs = item.Logs ?? [];
        for (int i = 0; i < logs.Length; i++)
        {
            logsLength += LogEntryDecoder.GetLength(logs[i]);
        }

        return logsLength;
    }

    public override int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
    {
        (int Total, _) = GetContentLength(item, rlpBehaviors);
        return LengthOfSequence(Total);
    }
}
