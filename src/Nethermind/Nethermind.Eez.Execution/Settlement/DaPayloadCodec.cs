// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The EEZ message stream carried in <c>batch.callData</c> (no blobs yet, so the calldata is the whole stream):
/// the protocol version, one <c>ChainOperation</c> whose opaque operations are the rollup's
/// <c>native_block_span_v0</c>, then one <c>Initiate</c>, <c>Call</c>, <c>Return</c>, <c>Finish</c> bracket per
/// action. Stream scalars are little-endian and its <c>bytes</c> lengths are varints that may be padded; span
/// varints must be minimal and span runs maximal, so a span has exactly one encoding.
/// </summary>
public static class DaPayloadCodec
{
    public const byte StreamVersion = 0x00;
    public const byte SpanVersion = 0x00;
    public const int MaxExtraData = 32;

    private const byte ChainOperation = 2;
    private const byte Initiate = 3;
    private const byte Call = 4;
    private const byte ReturnSuccess = 6;
    private const byte ReturnFail = 7;
    private const byte Finish = 10;
    private const int MaxVarintBytes = 5;

    /// <exception cref="EezSettlementException">The stream or its span is malformed.</exception>
    public static DaPayload Decode(ReadOnlyMemory<byte> payload)
    {
        Reader reader = new(payload);
        if (payload.Length == 0)
        {
            throw Invalid("the payload is empty");
        }

        if (reader.Byte("stream version") != StreamVersion)
        {
            throw Invalid("unsupported stream version");
        }

        reader.Expect(ChainOperation);
        ulong rollupId = reader.UInt64("chain id");
        DaSpan span = DecodeSpan(reader.Bytes("operations", padded: true));

        List<DaAction> actions = [];
        while (reader.Remaining != 0)
        {
            actions.Add(DecodeAction(ref reader));
        }

        return new DaPayload(rollupId, span, actions.ToArray());
    }

    /// <exception cref="EezSettlementException">The span is empty or a field exceeds its limit.</exception>
    public static byte[] Encode(ulong rollupId, IReadOnlyList<(Address Beneficiary, byte[] ExtraData, IReadOnlyList<byte[]> Transactions)> blocks,
        IReadOnlyList<DaAction> actions)
    {
        List<byte> spanBytes = EncodeSpan(blocks);
        List<byte> stream = [StreamVersion, ChainOperation];
        AddUInt64(stream, rollupId);
        AddBytes(stream, spanBytes.ToArray());
        Span<byte> value = stackalloc byte[32];
        foreach (DaAction action in actions)
        {
            stream.Add(Initiate);
            AddUInt64(stream, action.SourceRollupId);
            AddBytes(stream, []);
            stream.Add(Call);
            AddUInt64(stream, action.TargetRollupId);
            stream.AddRange(action.SourceAddress.Bytes);
            stream.AddRange(action.TargetAddress.Bytes);
            action.Value.ToLittleEndian(value);
            stream.AddRange(value);
            AddUInt64(stream, action.Gas);
            AddBytes(stream, action.Data);
            stream.Add(action.Success ? ReturnSuccess : ReturnFail);
            AddBytes(stream, action.ReturnData);
            stream.Add(Finish);
        }

        return stream.ToArray();
    }

    private static DaAction DecodeAction(ref Reader reader)
    {
        reader.Expect(Initiate);
        ulong sourceRollupId = reader.UInt64("initiate chain id");
        reader.Bytes("tx data", padded: true);
        reader.Expect(Call);
        ulong targetRollupId = reader.UInt64("to chain");
        Address sourceAddress = new(reader.Take("from address", Address.Size).ToArray());
        Address targetAddress = new(reader.Take("to address", Address.Size).ToArray());
        UInt256 value = new(reader.Take("value", 32).Span, isBigEndian: false);
        ulong gas = reader.UInt64("gas");
        byte[] data = reader.Bytes("data", padded: true).ToArray();
        bool success = reader.Byte("return message") switch
        {
            ReturnSuccess => true,
            ReturnFail => false,
            byte other => throw Invalid($"unknown message type {other}"),
        };
        byte[] returnData = reader.Bytes("return data", padded: true).ToArray();
        reader.Expect(Finish);
        return new DaAction(sourceRollupId, targetRollupId, sourceAddress, targetAddress, value, gas, data, success, returnData);
    }

    private static DaSpan DecodeSpan(ReadOnlyMemory<byte> operations)
    {
        Reader reader = new(operations);
        if (operations.Length == 0 || reader.Byte("span version") != SpanVersion)
        {
            throw Invalid("unsupported span version");
        }

        int blockCount = reader.Varint("block count", padded: false);
        if (blockCount == 0)
        {
            throw Invalid("the span has no blocks");
        }

        reader.EnsurePlausible("block count", blockCount, 1);
        int[] counts = new int[blockCount];
        long totalTransactions = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = reader.Varint("transaction count", padded: false);
            totalTransactions += counts[i];
        }

        Address[] beneficiaries = DecodeRuns(ref reader, blockCount, "beneficiary", static (ref Reader r) => new Address(r.Take("beneficiary", Address.Size).ToArray()));
        ReadOnlyMemory<byte>[] extraData = DecodeRuns(ref reader, blockCount, "extra data", static (ref Reader r) =>
        {
            int length = r.Byte("extra data length");
            return length > MaxExtraData ? throw Invalid($"extra data length {length} exceeds {MaxExtraData}") : r.Take("extra data", length);
        });

        reader.EnsurePlausible("transaction count", totalTransactions, 2);
        int[] lengths = new int[totalTransactions];
        long totalBytes = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = reader.Varint("transaction length", padded: false);
            if (lengths[i] == 0)
            {
                throw Invalid($"transaction {i} is empty");
            }

            totalBytes += lengths[i];
        }

        if (totalBytes != reader.Remaining)
        {
            throw Invalid($"the transactions declare {totalBytes} bytes, but {reader.Remaining} remain");
        }

        ReadOnlyMemory<byte>[] transactions = new ReadOnlyMemory<byte>[lengths.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = reader.Take("transaction", lengths[i]);
        }

        return new DaSpan(counts, beneficiaries, extraData, transactions);
    }

    private delegate T ReadValue<out T>(ref Reader reader);

    private static T[] DecodeRuns<T>(ref Reader reader, int blockCount, string field, ReadValue<T> read)
    {
        T[] values = new T[blockCount];
        int filled = 0;
        bool hasPrevious = false;
        T previous = default!;
        while (filled < blockCount)
        {
            int runLength = reader.Varint(field, padded: false);
            if (runLength == 0)
            {
                throw Invalid($"a {field} run is empty");
            }

            T value = read(ref reader);
            if (hasPrevious && RunValuesEqual(previous, value))
            {
                throw Invalid($"adjacent {field} runs repeat a value");
            }

            if ((long)filled + runLength > blockCount)
            {
                throw Invalid($"{field} runs cover more than the span's blocks");
            }

            values.AsSpan(filled, runLength).Fill(value);
            filled += runLength;
            previous = value;
            hasPrevious = true;
        }

        return values;
    }

    private static bool RunValuesEqual<T>(T left, T right) => (left, right) switch
    {
        (ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b) => a.Span.SequenceEqual(b.Span),
        _ => EqualityComparer<T>.Default.Equals(left, right),
    };

    private static List<byte> EncodeSpan(IReadOnlyList<(Address Beneficiary, byte[] ExtraData, IReadOnlyList<byte[]> Transactions)> blocks)
    {
        if (blocks.Count == 0)
        {
            throw Invalid("the span has no blocks");
        }

        List<byte> span = [SpanVersion];
        AddVarint(span, blocks.Count);
        foreach ((_, _, IReadOnlyList<byte[]> transactions) in blocks)
        {
            AddVarint(span, transactions.Count);
        }

        for (int start = 0; start < blocks.Count;)
        {
            int end = start + 1;
            while (end < blocks.Count && blocks[end].Beneficiary == blocks[start].Beneficiary)
            {
                end++;
            }

            AddVarint(span, end - start);
            span.AddRange(blocks[start].Beneficiary.Bytes);
            start = end;
        }

        for (int start = 0; start < blocks.Count;)
        {
            byte[] extraData = blocks[start].ExtraData;
            if (extraData.Length > MaxExtraData)
            {
                throw Invalid($"extra data length {extraData.Length} exceeds {MaxExtraData}");
            }

            int end = start + 1;
            while (end < blocks.Count && blocks[end].ExtraData.AsSpan().SequenceEqual(extraData))
            {
                end++;
            }

            AddVarint(span, end - start);
            span.Add((byte)extraData.Length);
            span.AddRange(extraData);
            start = end;
        }

        foreach ((_, _, IReadOnlyList<byte[]> transactions) in blocks)
        {
            foreach (byte[] transaction in transactions)
            {
                AddVarint(span, transaction.Length);
            }
        }

        foreach ((_, _, IReadOnlyList<byte[]> transactions) in blocks)
        {
            foreach (byte[] transaction in transactions)
            {
                span.AddRange(transaction);
            }
        }

        return span;
    }

    private static void AddUInt64(List<byte> output, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        output.AddRange(bytes);
    }

    private static void AddBytes(List<byte> output, byte[] value)
    {
        AddVarint(output, value.Length);
        output.AddRange(value);
    }

    private static void AddVarint(List<byte> output, int value)
    {
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            output.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        output.Add((byte)remaining);
    }

    private static EezSettlementException Invalid(string reason) => new($"Invalid DA payload: {reason}.");

    private struct Reader(ReadOnlyMemory<byte> data)
    {
        private int _position;

        public readonly int Remaining => data.Length - _position;

        public ReadOnlyMemory<byte> Take(string field, int length)
        {
            if (length > Remaining)
            {
                throw Invalid($"truncated while reading {field}");
            }

            ReadOnlyMemory<byte> slice = data.Slice(_position, length);
            _position += length;
            return slice;
        }

        public byte Byte(string field) => Take(field, 1).Span[0];

        public ulong UInt64(string field) => BinaryPrimitives.ReadUInt64LittleEndian(Take(field, sizeof(ulong)).Span);

        public void Expect(byte message)
        {
            if (Byte("message type") != message)
            {
                throw Invalid($"expected message type {message}");
            }
        }

        public ReadOnlyMemory<byte> Bytes(string field, bool padded) => Take(field, Varint(field, padded));

        /// <summary>A <c>uint32</c> in one to five bytes; unless <paramref name="padded"/>, only its shortest form.</summary>
        public int Varint(string field, bool padded)
        {
            ulong value = 0;
            for (int i = 0; i < MaxVarintBytes; i++)
            {
                byte current = Byte(field);
                ulong group = current & 0x7fu;
                if (i == MaxVarintBytes - 1 && group > 0x0f)
                {
                    throw Invalid($"varint out of range while reading {field}");
                }

                value |= group << (7 * i);
                if ((current & 0x80) == 0)
                {
                    if (!padded && i > 0 && group == 0)
                    {
                        throw Invalid($"padded varint while reading {field}");
                    }

                    return value <= int.MaxValue ? (int)value : throw Invalid($"{field} {value} is out of range");
                }
            }

            throw Invalid($"varint too long while reading {field}");
        }

        public readonly void EnsurePlausible(string field, long count, int bytesPerElement)
        {
            if (count > Remaining / bytesPerElement)
            {
                throw Invalid($"{field} {count} exceeds what the {Remaining} remaining bytes can encode");
            }
        }
    }
}
