// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.TraceStore.Ots;

internal static class TracePositionRecord
{
    internal const int Version = 1;
    internal const int MaxBlobBytes = 2 * 1024 * 1024;
    internal const int MaxTransactions = 16384;
    internal const int MaxNodes = 32768;
    internal const int MaxAddresses = 8192;
    internal const int MaxPositions = 65536;
    internal const int MaxRecordBytes = 512 * 1024;
    private const int HeaderSize = 132;
    private const int DigestSize = 32;

    internal static byte[]? Create(Block block, List<ParityLikeTxTrace> traces, ReadOnlySpan<byte> blob, CancellationToken ct)
    {
        if (block.Transactions.Length > MaxTransactions || blob.Length > MaxBlobBytes) return null;
        if (OtsTraceReader.Validate(block, traces, ct) is { } error) throw new InvalidDataException(error);
        Dictionary<Address, HashSet<int>> groups = [];
        Stack<ParityTraceAction> pending = new();
        int nodes = 0;
        int total = 0;
        foreach (ParityLikeTxTrace trace in traces)
        {
            ct.ThrowIfCancellationRequested();
            if (trace.TransactionHash is null) continue;
            int position = trace.TransactionPosition!.Value;
            pending.Push(trace.Action!);
            while (pending.TryPop(out ParityTraceAction? action))
            {
                ct.ThrowIfCancellationRequested();
                if (++nodes > MaxNodes || action is null || action.Subtraces is null) return null;
                if (!Add(action.From, position) || !Add(action.To, position) || !Add(action.Result?.Address, position)) return null;
                if ((long)nodes + pending.Count + action.Subtraces.Count > MaxNodes) return null;
                foreach (ParityTraceAction child in action.Subtraces) pending.Push(child);
            }
        }

        int length = checked(HeaderSize + DigestSize + groups.Count * 24 + total * 4);
        if (length > MaxRecordBytes) return null;
        byte[] record = new byte[length];
        "OTP1"u8.CopyTo(record);
        Write(record, 4, Version);
        Write(record, 8, Version);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(12), block.Number);
        block.Hash!.Bytes.CopyTo(record.AsSpan(20, 32));
        Write(record, 52, block.Transactions.Length);
        Write(record, 56, blob.Length);
        Write(record, 60, groups.Count);
        Write(record, 64, total);
        SHA256.HashData(blob, record.AsSpan(68, 32));
        HashTransactions(block, record.AsSpan(100, 32), ct);
        Address[] addresses = new Address[groups.Count];
        groups.Keys.CopyTo(addresses, 0);
        Array.Sort(addresses, static (a, b) => a.Bytes.SequenceCompareTo(b.Bytes));
        int offset = HeaderSize;
        foreach (Address address in addresses)
        {
            ct.ThrowIfCancellationRequested();
            address.Bytes.CopyTo(record.AsSpan(offset, 20));
            HashSet<int> set = groups[address];
            Write(record, offset + 20, set.Count);
            offset += 24;
            int[] positions = new int[set.Count];
            set.CopyTo(positions);
            Array.Sort(positions);
            foreach (int position in positions)
            {
                Write(record, offset, position);
                offset += 4;
            }
        }
        SHA256.HashData(record.AsSpan(0, offset), record.AsSpan(offset));
        return record;

        bool Add(Address? address, int position)
        {
            if (address is null) return true;
            if (!groups.TryGetValue(address, out HashSet<int>? positions))
            {
                if (groups.Count == MaxAddresses) return false;
                groups.Add(address, positions = []);
            }
            return !positions.Add(position) || ++total <= MaxPositions;
        }
    }

    internal static bool[]? Match(ReadOnlySpan<byte> record, Block block, Address address, ReadOnlySpan<byte> blob, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (record.Length < 8 || !record[..4].SequenceEqual("OTP1"u8)) throw Invalid();
        if (Read(record, 4) != Version) return null;
        if (record.Length < 12) throw Invalid();
        if (Read(record, 8) != Version) return null;
        if (record.Length < HeaderSize + DigestSize || record.Length > MaxRecordBytes) throw Invalid();
        int transactions = Read(record, 52);
        int addresses = Read(record, 60);
        int total = Read(record, 64);
        if ((uint)transactions > MaxTransactions || transactions != block.Transactions.Length
            || (uint)addresses > MaxAddresses || (uint)total > MaxPositions
            || Read(record, 56) != blob.Length || blob.IsEmpty || blob.Length > MaxBlobBytes
            || BinaryPrimitives.ReadUInt64LittleEndian(record[12..]) != block.Number
            || !record.Slice(20, 32).SequenceEqual(block.Hash!.Bytes)
            || record.Length != HeaderSize + DigestSize + addresses * 24 + total * 4) throw Invalid();
        Span<byte> digest = stackalloc byte[DigestSize];
        SHA256.HashData(record[..^DigestSize], digest);
        if (!digest.SequenceEqual(record[^DigestSize..])) throw Invalid();
        SHA256.HashData(blob, digest);
        if (!digest.SequenceEqual(record.Slice(68, 32))) throw Invalid();
        HashTransactions(block, digest, ct);
        if (!digest.SequenceEqual(record.Slice(100, 32))) throw Invalid();

        int offset = HeaderSize;
        int foundOffset = 0;
        int foundCount = 0;
        int counted = 0;
        ReadOnlySpan<byte> previousAddress = default;
        for (int i = 0; i < addresses; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (offset > record.Length - DigestSize - 24) throw Invalid();
            ReadOnlySpan<byte> current = record.Slice(offset, 20);
            if (i != 0 && previousAddress.SequenceCompareTo(current) >= 0) throw Invalid();
            previousAddress = current;
            int count = Read(record, offset + 20);
            offset += 24;
            if (count <= 0 || count > total - counted || count > (record.Length - DigestSize - offset) / 4) throw Invalid();
            if (current.SequenceEqual(address.Bytes)) (foundOffset, foundCount) = (offset, count);
            int previous = -1;
            for (int j = 0; j < count; j++)
            {
                ct.ThrowIfCancellationRequested();
                int position = Read(record, offset);
                if ((uint)position >= (uint)transactions || position <= previous) throw Invalid();
                previous = position;
                offset += 4;
            }
            counted += count;
        }
        if (counted != total || offset != record.Length - DigestSize) throw Invalid();
        bool[] matches = new bool[transactions];
        for (int i = 0; i < foundCount; i++) matches[Read(record, foundOffset + i * 4)] = true;
        ct.ThrowIfCancellationRequested();
        return matches;
    }

    private static void HashTransactions(Block block, Span<byte> destination, CancellationToken ct)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Transaction transaction in block.Transactions)
        {
            ct.ThrowIfCancellationRequested();
            if (transaction.Hash is not { } transactionHash) throw Invalid();
            hash.AppendData(transactionHash.Bytes);
        }
        hash.GetHashAndReset(destination);
    }

    private static int Read(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadInt32LittleEndian(value[offset..]);
    private static void Write(Span<byte> value, int offset, int number) => BinaryPrimitives.WriteInt32LittleEndian(value[offset..], number);
    private static InvalidDataException Invalid() => new("Invalid trace position proof.");
}
