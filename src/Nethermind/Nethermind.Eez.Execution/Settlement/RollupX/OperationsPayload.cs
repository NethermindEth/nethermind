// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>
/// Consecutive blocks with the same beneficiary and extra data: either one block with ordinary transactions, or
/// <see cref="Blocks"/> blocks without any.
/// </summary>
public readonly record struct OperationsRun(uint Blocks, Address Beneficiary, ReadOnlyMemory<byte> ExtraData, ReadOnlyMemory<byte>[] Transactions);

/// <summary>
/// The direct columnar V0 operations of a RollupX <c>ChainOperation</c>: a block count, transaction-count
/// tokens that run-length code empty blocks, maximal beneficiary and extra-data runs, then the transaction
/// lengths and bytes. Only each block's ordinary transactions travel; the terminal Sync block carries just its
/// ordinary prefix. Decoding never allocates by the declared block count, which may reach <see cref="uint.MaxValue"/>.
/// </summary>
public sealed class OperationsPayload
{
    public const byte Version = 0x00;
    public const int MaxTransactionsPerBlock = 16_383;
    public const int MaxExtraData = 32;

    private const byte EmptyBlock = 0x00;
    private const byte FirstTwoByteCount = 0x80;
    private const byte FirstEmptyRun = 0xc0;
    private const byte LongEmptyRun = 0xff;
    private const int ShortEmptyRunMax = 64;

    private readonly CountRun[] _counts;
    private readonly MetadataRun<Address>[] _beneficiaries;
    private readonly MetadataRun<ReadOnlyMemory<byte>>[] _extraData;
    private readonly ReadOnlyMemory<byte>[] _transactions;

    private OperationsPayload(uint blockCount, CountRun[] counts, MetadataRun<Address>[] beneficiaries,
        MetadataRun<ReadOnlyMemory<byte>>[] extraData, ReadOnlyMemory<byte>[] transactions)
    {
        BlockCount = blockCount;
        _counts = counts;
        _beneficiaries = beneficiaries;
        _extraData = extraData;
        _transactions = transactions;
    }

    public uint BlockCount { get; }

    public int TransactionCount => _transactions.Length;

    /// <exception cref="EezSettlementException">The operations are malformed or not canonical.</exception>
    public static OperationsPayload Decode(ReadOnlyMemory<byte> operations)
    {
        DaReader reader = new(operations);
        if (operations.Length == 0 || reader.Byte("operations version") != Version)
        {
            throw DaReader.Invalid("unsupported operations version");
        }

        uint blockCount = reader.UInt32Varint("block count", padded: false);
        if (blockCount == 0)
        {
            throw DaReader.Invalid("the operations cover no blocks");
        }

        CountRun[] counts = DecodeCounts(ref reader, blockCount, out long transactionCount);
        MetadataRun<Address>[] beneficiaries = DecodeMetadata(ref reader, blockCount, "beneficiary",
            static (ref DaReader r) => new Address(r.Take("beneficiary", Address.Size).ToArray()), static (a, b) => a == b);
        MetadataRun<ReadOnlyMemory<byte>>[] extraData = DecodeMetadata(ref reader, blockCount, "extra data", ReadExtraData,
            static (a, b) => a.Span.SequenceEqual(b.Span));
        reader.EnsurePlausible("transaction count", transactionCount, 2);
        ReadOnlyMemory<byte>[] transactions = DecodeTransactions(ref reader, (int)transactionCount);
        return new OperationsPayload(blockCount, counts, beneficiaries, extraData, transactions);
    }

    /// <summary>
    /// The blocks as runs with one beneficiary and extra data each; a block with transactions is a run of its own.
    /// </summary>
    public IEnumerable<OperationsRun> Runs()
    {
        int beneficiary = 0;
        int extraData = 0;
        uint beneficiaryLeft = _beneficiaries[0].Blocks;
        uint extraDataLeft = _extraData[0].Blocks;
        int transaction = 0;
        foreach (CountRun count in _counts)
        {
            uint left = count.Blocks;
            while (left != 0)
            {
                uint blocks = Math.Min(left, Math.Min(beneficiaryLeft, extraDataLeft));
                ReadOnlyMemory<byte>[] transactions = _transactions.AsSpan(transaction, count.Transactions).ToArray();
                transaction += count.Transactions;
                yield return new OperationsRun(blocks, _beneficiaries[beneficiary].Value, _extraData[extraData].Value, transactions);

                left -= blocks;
                beneficiaryLeft -= blocks;
                extraDataLeft -= blocks;
                if (beneficiaryLeft == 0 && ++beneficiary < _beneficiaries.Length)
                {
                    beneficiaryLeft = _beneficiaries[beneficiary].Blocks;
                }

                if (extraDataLeft == 0 && ++extraData < _extraData.Length)
                {
                    extraDataLeft = _extraData[extraData].Blocks;
                }
            }
        }
    }

    /// <exception cref="EezSettlementException">The runs are empty, a run with transactions spans several blocks, or a field exceeds its limit.</exception>
    public static byte[] Encode(IReadOnlyList<OperationsRun> runs)
    {
        uint blockCount = CountBlocks(runs);
        List<byte> output = [Version];
        AddVarint(output, blockCount);
        EncodeCounts(output, runs);
        EncodeMetadata(output, runs, static run => run.Beneficiary.Bytes.ToArray(), static (a, b) => a.Beneficiary == b.Beneficiary, variable: false);
        EncodeMetadata(output, runs, static run => run.ExtraData.ToArray(), static (a, b) => a.ExtraData.Span.SequenceEqual(b.ExtraData.Span), variable: true);
        foreach (OperationsRun run in runs)
        {
            foreach (ReadOnlyMemory<byte> transaction in run.Transactions)
            {
                AddVarint(output, (uint)transaction.Length);
            }
        }

        foreach (OperationsRun run in runs)
        {
            foreach (ReadOnlyMemory<byte> transaction in run.Transactions)
            {
                output.AddRange(transaction.Span);
            }
        }

        return output.ToArray();
    }

    private static CountRun[] DecodeCounts(ref DaReader reader, uint blockCount, out long transactionCount)
    {
        List<CountRun> runs = [];
        uint covered = 0;
        long transactions = 0;
        bool previousEmpty = false;
        while (covered < blockCount)
        {
            CountRun run = ReadCountToken(ref reader);
            bool empty = run.Transactions == 0;
            if (empty && previousEmpty)
            {
                throw DaReader.Invalid("adjacent empty-block tokens");
            }

            if (run.Blocks > blockCount - covered)
            {
                throw DaReader.Invalid("transaction-count tokens cover more than the declared blocks");
            }

            covered += run.Blocks;
            transactions += run.Transactions;
            previousEmpty = empty;
            runs.Add(run);
        }

        transactionCount = transactions;
        return runs.ToArray();
    }

    private static CountRun ReadCountToken(ref DaReader reader)
    {
        byte first = reader.Byte("transaction-count token");
        switch (first)
        {
            case EmptyBlock:
                return new CountRun(1, 0);
            case < FirstTwoByteCount:
                return new CountRun(1, first);
            case < FirstEmptyRun:
                int count = ((first & 0x3f) << 8) | reader.Byte("transaction-count token");
                return count >= FirstTwoByteCount ? new CountRun(1, count) : throw DaReader.Invalid($"two-byte transaction count {count} fits one byte");
            case < LongEmptyRun:
                return new CountRun((uint)(first - FirstEmptyRun + 2), 0);
            default:
                uint blocks = reader.UInt32Varint("empty-block run", padded: false);
                return blocks > ShortEmptyRunMax ? new CountRun(blocks, 0) : throw DaReader.Invalid($"long empty-block run {blocks} fits a short token");
        }
    }

    private delegate T ReadValue<out T>(ref DaReader reader);

    private static MetadataRun<T>[] DecodeMetadata<T>(ref DaReader reader, uint blockCount, string field, ReadValue<T> read, Func<T, T, bool> equal)
    {
        List<MetadataRun<T>> runs = [];
        uint covered = 0;
        while (covered < blockCount)
        {
            uint blocks = reader.UInt32Varint(field, padded: false);
            if (blocks == 0 || blocks > blockCount - covered)
            {
                throw DaReader.Invalid($"a {field} run is empty or covers more than the declared blocks");
            }

            T value = read(ref reader);
            if (runs.Count != 0 && equal(runs[^1].Value, value))
            {
                throw DaReader.Invalid($"adjacent {field} runs repeat a value");
            }

            covered += blocks;
            runs.Add(new MetadataRun<T>(blocks, value));
        }

        return runs.ToArray();
    }

    private static ReadOnlyMemory<byte> ReadExtraData(ref DaReader reader)
    {
        int length = reader.Byte("extra data length");
        return length > MaxExtraData ? throw DaReader.Invalid($"extra data length {length} exceeds {MaxExtraData}") : reader.Take("extra data", length);
    }

    private static ReadOnlyMemory<byte>[] DecodeTransactions(ref DaReader reader, int count)
    {
        int[] lengths = new int[count];
        long total = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = reader.Varint("transaction length", padded: false);
            total += lengths[i];
            if (lengths[i] == 0 || total > reader.Remaining)
            {
                throw DaReader.Invalid($"transaction {i} is empty or longer than the remaining bytes");
            }
        }

        if (total != reader.Remaining)
        {
            throw DaReader.Invalid($"the transactions declare {total} bytes, but {reader.Remaining} remain");
        }

        ReadOnlyMemory<byte>[] transactions = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = reader.Take("transaction", lengths[i]);
            EnsureOrdinary(transactions[i], i);
        }

        return transactions;
    }

    /// <summary>A canonical EIP-2718 transaction that is neither a native system transaction nor a blob transaction.</summary>
    private static void EnsureOrdinary(ReadOnlyMemory<byte> encoded, int index)
    {
        byte type = encoded.Span[0];
        if (type == (byte)EezConstants.SystemTxType || type == (byte)TxType.Blob)
        {
            throw DaReader.Invalid($"transaction {index} has type {type}, which cannot travel as an ordinary transaction");
        }

        Transaction transaction;
        try
        {
            RlpReader reader = new(encoded.Span);
            transaction = TxDecoder.Instance.Decode(ref reader, RlpBehaviors.ExcludeHashes | RlpBehaviors.SkipTypedWrapping)!;
        }
        catch (RlpException e)
        {
            throw DaReader.Invalid($"transaction {index} does not decode: {e.Message}");
        }

        if (!TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.AsSpan().SequenceEqual(encoded.Span))
        {
            throw DaReader.Invalid($"transaction {index} is not canonically encoded");
        }
    }

    private static uint CountBlocks(IReadOnlyList<OperationsRun> runs)
    {
        ulong blocks = 0;
        foreach (OperationsRun run in runs)
        {
            if (run.Blocks == 0 || (run.Transactions.Length != 0 && run.Blocks != 1))
            {
                throw DaReader.Invalid("a run must have blocks, and a run with transactions exactly one");
            }

            if (run.Transactions.Length > MaxTransactionsPerBlock || run.ExtraData.Length > MaxExtraData)
            {
                throw DaReader.Invalid("a block exceeds the transaction or extra data limit");
            }

            blocks += run.Blocks;
        }

        return blocks is > 0 and <= uint.MaxValue ? (uint)blocks : throw DaReader.Invalid($"the operations cover {blocks} blocks");
    }

    private static void EncodeCounts(List<byte> output, IReadOnlyList<OperationsRun> runs)
    {
        uint empty = 0;
        foreach (OperationsRun run in runs)
        {
            if (run.Transactions.Length == 0)
            {
                empty += run.Blocks;
                continue;
            }

            AddEmptyRun(output, empty);
            empty = 0;
            int count = run.Transactions.Length;
            if (count < FirstTwoByteCount)
            {
                output.Add((byte)count);
            }
            else
            {
                output.Add((byte)(FirstTwoByteCount | (count >> 8)));
                output.Add((byte)count);
            }
        }

        AddEmptyRun(output, empty);
    }

    private static void AddEmptyRun(List<byte> output, uint blocks)
    {
        switch (blocks)
        {
            case 0:
                return;
            case 1:
                output.Add(EmptyBlock);
                return;
            case <= ShortEmptyRunMax:
                output.Add((byte)(FirstEmptyRun + blocks - 2));
                return;
            default:
                output.Add(LongEmptyRun);
                AddVarint(output, blocks);
                return;
        }
    }

    private static void EncodeMetadata(List<byte> output, IReadOnlyList<OperationsRun> runs, Func<OperationsRun, byte[]> value,
        Func<OperationsRun, OperationsRun, bool> equal, bool variable)
    {
        for (int start = 0; start < runs.Count;)
        {
            uint blocks = runs[start].Blocks;
            int end = start + 1;
            while (end < runs.Count && equal(runs[end], runs[start]))
            {
                blocks += runs[end].Blocks;
                end++;
            }

            AddVarint(output, blocks);
            byte[] bytes = value(runs[start]);
            if (variable)
            {
                output.Add((byte)bytes.Length);
            }

            output.AddRange(bytes);
            start = end;
        }
    }

    private static void AddVarint(List<byte> output, uint value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    private readonly record struct CountRun(uint Blocks, int Transactions);

    private readonly record struct MetadataRun<T>(uint Blocks, T Value);
}
