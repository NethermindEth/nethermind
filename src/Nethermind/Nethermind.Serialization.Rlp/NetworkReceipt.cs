// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using BloomFilter = Nethermind.Core.Bloom;

namespace Nethermind.Serialization.Rlp;

/// <summary>A receipt as a receipts message carries it, read without decoding its logs.</summary>
/// <remarks>
/// <see cref="TryRead"/> reads the receipts of the transaction types from <see cref="TxType.Legacy"/> to
/// <see cref="TxType.SetCode"/> whose first field is a status or a post-transaction state in its canonical form, and
/// checks the logs as <see cref="LogEntryDecoder"/> does. It leaves every other shape, such as frame transaction or
/// deposit receipts, to <see cref="ReceiptMessageDecoder"/> by returning <see langword="false"/>.
/// </remarks>
public readonly ref struct NetworkReceipt
{
    private const int BloomItemLength = 3 + BloomFilter.ByteLength;
    private const int PostTransactionStateItemLength = 1 + Hash256.Size;

    private NetworkReceipt(TxType type, ReadOnlySpan<byte> statusOrState, ReadOnlySpan<byte> gasUsedTotalItem, ulong gasUsedTotal,
        ReadOnlySpan<byte> bloom, ReadOnlySpan<byte> logs, ulong logsGas)
    {
        Type = type;
        StatusOrState = statusOrState;
        GasUsedTotalItem = gasUsedTotalItem;
        GasUsedTotal = gasUsedTotal;
        Bloom = bloom;
        Logs = logs;
        LogsGas = logsGas;
    }

    public TxType Type { get; }

    /// <summary>The encoded status, or the encoded post-transaction state before EIP-658.</summary>
    public ReadOnlySpan<byte> StatusOrState { get; }

    public ReadOnlySpan<byte> GasUsedTotalItem { get; }

    /// <summary>The block's gas used up to this receipt, or its own gas used under EIP-8116.</summary>
    public ulong GasUsedTotal { get; }

    /// <summary>The bloom bytes as received; empty when the message has none, as from eth/69 on.</summary>
    public ReadOnlySpan<byte> Bloom { get; }

    /// <summary>The encoded log sequence.</summary>
    public ReadOnlySpan<byte> Logs { get; }

    /// <summary>The log gas the receipt's logs cost, as <c>LOG</c> instructions charge it.</summary>
    public ulong LogsGas { get; }

    /// <summary>Reads the next receipt of <paramref name="reader"/> when it has a shape this reader takes.</summary>
    /// <param name="withBloom">Whether the receipt has a bloom: the eth/63 to eth/68 form; otherwise the eth/69 form.</param>
    /// <returns><see langword="false"/> for a shape left to <see cref="ReceiptMessageDecoder"/>; the reader is then not usable.</returns>
    /// <exception cref="RlpReaderException">The receipt is not valid RLP.</exception>
    public static bool TryRead(ref RlpReader reader, bool withBloom, out NetworkReceipt receipt)
    {
        receipt = default;
        if (!reader.HasNext || reader.IsNextItemEmptyList()) return false;

        TxType type;
        RlpReader fields;
        int receiptLength = 0;
        if (withBloom)
        {
            ReadOnlySpan<byte> payload;
            if (reader.IsNextSequence)
            {
                type = TxType.Legacy;
                payload = reader.ReadEncodedItem();
            }
            else
            {
                // A bare type byte is the receipt trie's form, not a message's.
                if (reader.PeekByte() < Rlp.EmptyByteArrayByte) return false;
                ReadOnlySpan<byte> envelope = reader.ReadBytes();
                if (envelope.IsEmpty) return false;
                type = (TxType)envelope[0];
                if (type == TxType.Legacy) return false;
                payload = envelope[1..];
            }

            RlpReader payloadReader = new(payload);
            fields = payloadReader.EnterSequence(out int payloadLength);
            if (payloadLength != payload.Length) return false;
        }
        else
        {
            fields = reader.EnterSequence(out receiptLength);
            type = (TxType)fields.DecodeByte();
        }

        if (type > TxType.SetCode) return false;

        ReadOnlySpan<byte> statusOrState = fields.ReadEncodedItem();
        if (!IsStatusOrState(statusOrState)) return false;

        ReadOnlySpan<byte> gasUsedTotalItem = fields.ReadEncodedItem();
        RlpReader gasReader = new(gasUsedTotalItem);
        ulong gasUsedTotal = gasReader.DecodeULong();

        ReadOnlySpan<byte> bloom = default;
        if (withBloom)
        {
            ReadOnlySpan<byte> bloomItem = fields.ReadEncodedItem();
            // A zero-length bloom (EIP-7668) is valid only where the decoder is told the fork is scheduled.
            if (bloomItem.Length != BloomItemLength || bloomItem[0] != 0xb9 || bloomItem[1] != 0x01 || bloomItem[2] != 0x00) return false;
            bloom = bloomItem[3..];
        }

        if (!fields.HasNext || !fields.IsNextSequence) return false;
        ReadOnlySpan<byte> logs = fields.ReadEncodedItem();
        if (fields.HasNext || !TryGetLogsGas(logs, out ulong logsGas)) return false;
        if (!withBloom) reader.ExitSequence(in fields, receiptLength);

        receipt = new NetworkReceipt(type, statusOrState, gasUsedTotalItem, gasUsedTotal, bloom, logs, logsGas);
        return true;
    }

    private static bool IsStatusOrState(ReadOnlySpan<byte> item) =>
        item.Length == 1 ? item[0] is Rlp.EmptyByteArrayByte or 0x01 : item.Length == PostTransactionStateItemLength && item[0] == 0xa0;

    private static bool TryGetLogsGas(ReadOnlySpan<byte> logs, out ulong logsGas)
    {
        logsGas = 0;
        RlpReader items = new RlpReader(logs).EnterSequence(out _);
        if (items.CountItems() > RlpLimit.ReceiptLogs.Limit) return false;
        while (items.HasNext)
        {
            if (!items.IsNextSequence || items.IsNextItemEmptyList()) return false;
            RlpReader log = items.EnterSequence(out int logLength);
            if (log.ReadBytes().Length != Address.Size || !log.HasNext || !log.IsNextSequence) return false;

            RlpReader topics = log.EnterSequence(out int topicsLength);
            int topicCount = 0;
            while (topics.HasNext)
            {
                if (++topicCount > 4 || topics.ReadBytes().Length != Hash256.Size) return false;
            }

            log.ExitSequence(in topics, topicsLength);
            if (!log.HasNext || log.IsNextSequence) return false;
            int dataLength = log.ReadBytes().Length;
            if (log.HasNext) return false;
            items.ExitSequence(in log, logLength);

            logsGas = checked(logsGas + GasCostOf.Log + (ulong)topicCount * GasCostOf.LogTopic + (ulong)dataLength * GasCostOf.LogData);
        }

        return true;
    }

    /// <summary>The length that <see cref="ReceiptMessageDecoder"/> gives the decoded receipt, bloom and typed wrapping included.</summary>
    /// <remarks>
    /// The decoder writes a status after EIP-658 and a post-transaction state before it, so the first field takes
    /// one byte unless it is a state written before EIP-658.
    /// </remarks>
    public int GetMessageLength(bool eip658)
    {
        int statusOrStateLength = !eip658 && StatusOrState.Length == PostTransactionStateItemLength ? PostTransactionStateItemLength : 1;
        int payloadLength = Rlp.LengthOfSequence(statusOrStateLength + GasUsedTotalItem.Length + BloomItemLength + Logs.Length);
        return Type == TxType.Legacy ? payloadLength : Rlp.LengthOfSequence(1 + payloadLength);
    }

    /// <summary>The length of the value the receipt trie stores for this receipt.</summary>
    public int GetTrieLeafLength(bool eip7668)
    {
        int payloadLength = Rlp.LengthOfSequence(GetTrieContentLength(eip7668));
        return Type == TxType.Legacy ? payloadLength : 1 + payloadLength;
    }

    private int GetTrieContentLength(bool eip7668) =>
        StatusOrState.Length + GasUsedTotalItem.Length + (eip7668 ? 1 : BloomItemLength) + Logs.Length;

    /// <summary>Writes the value the receipt trie stores for this receipt.</summary>
    /// <remarks>
    /// The fields are written as received. The bloom is the received one, or the one computed from the logs when the
    /// message has none or an all-zero one, as old receipts sync takes it; EIP-7668 receipts have a zero-length bloom.
    /// </remarks>
    public void WriteTrieLeaf(scoped ref RlpWriter writer, bool eip7668)
    {
        if (Type != TxType.Legacy) writer.WriteByte((byte)Type);
        RlpSequence sequence = writer.StartSequence(GetTrieContentLength(eip7668));
        writer.WriteEncoded(StatusOrState);
        writer.WriteEncoded(GasUsedTotalItem);
        if (eip7668)
        {
            writer.WriteByte(Rlp.EmptyByteArrayByte);
        }
        else if (Bloom.IsEmpty || !Bloom.ContainsAnyExcept((byte)0))
        {
            writer.Encode(CalculateBloom().Bytes);
        }
        else
        {
            writer.Encode(Bloom);
        }

        writer.WriteEncoded(Logs);
        writer.EndSequence(sequence);
    }

    private BloomFilter CalculateBloom()
    {
        BloomFilter bloom = new();
        RlpReader items = new RlpReader(Logs).EnterSequence(out _);
        while (items.HasNext)
        {
            RlpReader log = items.EnterSequence(out int logLength);
            bloom.Set(log.ReadBytes());
            RlpReader topics = log.EnterSequence(out int topicsLength);
            while (topics.HasNext) bloom.Set(topics.ReadBytes());
            log.ExitSequence(in topics, topicsLength);
            log.SkipItem();
            items.ExitSequence(in log, logLength);
        }

        return bloom;
    }
}
