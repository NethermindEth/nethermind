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
    IRlpStreamDecoder<OptimismTxReceipt>, IRlpValueDecoder<OptimismTxReceipt>, IRlpObjectDecoder<OptimismTxReceipt>, IReceiptRefDecoder,
    IRlpStreamDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>, IRlpObjectDecoder<TxReceipt>
{
    public OptimismTxReceipt Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null!;
        }

        OptimismTxReceipt txReceipt = new();
        int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;

        byte[] firstItem = rlpStream.DecodeByteArray();
        if (firstItem.Length == 1)
        {
            txReceipt.StatusCode = firstItem[0];
        }
        else
        {
            txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Hash256(firstItem);
        }

        txReceipt.Sender = rlpStream.DecodeAddress();
        txReceipt.GasUsedTotal = (long)rlpStream.DecodeUBigInt();

        int sequenceLength = rlpStream.ReadSequenceLength();
        int logEntriesCheck = sequenceLength + rlpStream.Position;
        using ArrayPoolList<LogEntry> logEntries = new(sequenceLength * 2 / LengthOfAddressRlp);

        while (rlpStream.Position < logEntriesCheck)
        {
            logEntries.Add(CompactLogEntryDecoder.Decode(rlpStream, RlpBehaviors.AllowExtraBytes)!);
        }

        txReceipt.Logs = [.. logEntries];

        if (lastCheck > rlpStream.Position)
        {
            int remainingItems = rlpStream.PeekNumberOfItemsRemaining(lastCheck);
            if (remainingItems > 0)
            {
                txReceipt.DepositNonce = rlpStream.DecodeUlong();
            }

            if (remainingItems > 1)
            {
                txReceipt.DepositReceiptVersion = rlpStream.DecodeUlong();
            }
        }

        bool allowExtraBytes = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0;
        if (!allowExtraBytes)
        {
            rlpStream.Check(lastCheck);
        }

        txReceipt.Bloom = new Bloom(txReceipt.Logs);

        return txReceipt;
    }

    public OptimismTxReceipt Decode(ref ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
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
        txReceipt.GasUsedTotal = (long)decoderContext.DecodeUBigInt();

        int sequenceLength = decoderContext.ReadSequenceLength();
        int logEntriesCheck = sequenceLength + decoderContext.Position;

        // Don't know the size exactly, I'll just assume its just an address and add some margin
        using ArrayPoolList<LogEntry> logEntries = new(sequenceLength * 2 / LengthOfAddressRlp);
        while (decoderContext.Position < logEntriesCheck)
        {
            logEntries.Add(CompactLogEntryDecoder.Decode(ref decoderContext, RlpBehaviors.AllowExtraBytes)!);
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

    public void DecodeStructRef(scoped ref ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors,
        out TxReceiptStructRef item)
    {
        // Note: This method runs at 2.5 million times/sec on my machine
        item = new TxReceiptStructRef();

        if (decoderContext.IsNextItemNull())
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
        item.GasUsedTotal = (long)decoderContext.DecodeUBigInt();

        (int PrefixLength, int ContentLength) =
            decoderContext.PeekPrefixAndContentLength();
        int logsBytes = ContentLength + PrefixLength;
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

    public void DecodeLogEntryStructRef(scoped ref ValueDecoderContext decoderContext, RlpBehaviors none,
        out LogEntryStructRef current)
    {
        CompactLogEntryDecoder.DecodeLogEntryStructRef(ref decoderContext, none, out current);
    }

    public Hash256[] DecodeTopics(ValueDecoderContext valueDecoderContext)
    {
        return CompactLogEntryDecoder.DecodeTopics(valueDecoderContext);
    }

    // Refstruct decode does not generate bloom
    public bool CanDecodeBloom => false;

    public Rlp Encode(OptimismTxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item!, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray()!);
    }

    public void Encode(RlpStream rlpStream, OptimismTxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            rlpStream.EncodeNullObject();
            return;
        }

        (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);

        bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

        // Note: Any byte saved here is about 3GB on mainnet.
        rlpStream.StartSequence(totalContentLength);
        if (isEip658receipts)
        {
            rlpStream.Encode(item.StatusCode);
        }
        else
        {
            rlpStream.Encode(item.PostTransactionState);
        }

        rlpStream.Encode(item.Sender);
        rlpStream.Encode(item.GasUsedTotal);

        rlpStream.StartSequence(logsLength);

        LogEntry[] logs = item.Logs ?? [];
        for (int i = 0; i < logs.Length; i++)
        {
            CompactLogEntryDecoder.Encode(rlpStream, logs[i]);
        }

        if (item.TxType == TxType.DepositTx && item.DepositNonce is not null)
        {
            rlpStream.Encode(item.DepositNonce.Value);

            if (item.DepositReceiptVersion is not null)
            {
                rlpStream.Encode(item.DepositReceiptVersion.Value);
            }
        }
    }

    private static (int Total, int Logs) GetContentLength(OptimismTxReceipt? item, RlpBehaviors rlpBehaviors)
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

        if (item.TxType == TxType.DepositTx && item.DepositNonce is not null)
        {
            contentLength += LengthOf(item.DepositNonce);

            if (item.DepositReceiptVersion is not null)
            {
                contentLength += LengthOf(item.DepositReceiptVersion.Value);
            }
        }

        return (contentLength, logsLength);
    }

    private static int GetLogsLength(OptimismTxReceipt item)
    {
        int logsLength = 0;
        LogEntry[] logs = item.Logs ?? [];
        for (int i = 0; i < logs.Length; i++)
        {
            logsLength += CompactLogEntryDecoder.Instance.GetLength(logs[i]);
        }

        return logsLength;
    }

    public int GetLength(OptimismTxReceipt item, RlpBehaviors rlpBehaviors)
    {
        (int Total, _) = GetContentLength(item, rlpBehaviors);
        return LengthOfSequence(Total);
    }

    TxReceipt IRlpStreamDecoder<TxReceipt>.Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        return Decode(rlpStream, rlpBehaviors);
    }

    public void Encode(RlpStream stream, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Encode(stream, (OptimismTxReceipt)item, rlpBehaviors);
    }

    public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
    {
        return GetLength((OptimismTxReceipt)item, rlpBehaviors);
    }

    TxReceipt IRlpValueDecoder<TxReceipt>.Decode(ref ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        return Decode(ref decoderContext, rlpBehaviors);
    }

    public Rlp Encode(TxReceipt? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Encode((OptimismTxReceipt?)item, rlpBehaviors);
    }
}
