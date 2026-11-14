// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>The receipts of several blocks as receipts messages carry them, decoded on the first read.</summary>
/// <remarks>
/// Every receipt is one <see cref="NetworkReceipt.TryRead"/> reads, so a consumer that stores the receipts without
/// using them can read <see cref="GetEncoded"/> and skip decoding the logs.
/// </remarks>
/// <param name="memory">Memory holding the encoded receipts, released on disposal.</param>
/// <param name="blocks">Where the encoded receipt sequence of each block is in <paramref name="memory"/>.</param>
/// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
/// <param name="decodeBlock">Decodes the receipt sequence of one block.</param>
public sealed class NetworkBlockReceipts(IMemoryOwner<byte> memory, Range[] blocks, bool withBloom, RefRlpReaderFunc<TxReceipt[]> decodeBlock)
    : IOwnedReadOnlyList<TxReceipt[]>
{
    private IMemoryOwner<byte>? _memory = memory;
    private readonly Range[] _blocks = blocks;
    private TxReceipt[][]? _decoded;

    /// <summary>Whether the receipts have blooms: the eth/63 to eth/68 form.</summary>
    public bool WithBloom { get; } = withBloom;

    public int Count => _blocks.Length;

    /// <summary>The encoded receipt sequence of the block at <paramref name="index"/>, valid until disposal.</summary>
    public ReadOnlySpan<byte> GetEncoded(int index) =>
        (_memory ?? throw new ObjectDisposedException(nameof(NetworkBlockReceipts))).Memory.Span[_blocks[index]];

    /// <summary>Takes the receipts of each block of <paramref name="encoded"/> when every receipt is one <see cref="NetworkReceipt.TryRead"/> reads.</summary>
    /// <param name="encoded">The encoded sequence of the blocks' receipt sequences.</param>
    /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
    /// <param name="decodeBlock">Decodes the receipt sequence of one block.</param>
    /// <returns>The receipts, or <see langword="null"/> when a receipt has a shape left to the receipt decoder.</returns>
    /// <exception cref="RlpReaderException">The receipts are not valid RLP.</exception>
    public static NetworkBlockReceipts? TryCreate(ReadOnlySpan<byte> encoded, bool withBloom, RefRlpReaderFunc<TxReceipt[]> decodeBlock, RlpLimit blocksLimit, RlpLimit receiptsLimit)
    {
        RlpReader blocks = new RlpReader(encoded).EnterSequence(out int itemLength);
        int blocksStart = itemLength - blocks.Remaining;
        Range[] ranges = new Range[blocks.CountItems(blocksLimit)];
        for (int i = 0; i < ranges.Length; i++)
        {
            int start = blocksStart + blocks.BytesRead;
            ReadOnlySpan<byte> block = blocks.ReadEncodedItem();
            RlpReader receipts = new RlpReader(block).EnterSequence(out _);
            receipts.CountItems(receiptsLimit);
            while (receipts.HasNext)
            {
                if (!NetworkReceipt.TryRead(ref receipts, withBloom, out _)) return null;
            }

            ranges[i] = new Range(start, start + block.Length);
        }

        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(encoded.Length);
        encoded.CopyTo(memory.Memory.Span);
        return new NetworkBlockReceipts(memory, ranges, withBloom, decodeBlock);
    }

    public TxReceipt[] this[int index] => AsSpan()[index];

    public ReadOnlySpan<TxReceipt[]> AsSpan() => _decoded ??= Decode();

    private TxReceipt[][] Decode()
    {
        TxReceipt[][] decoded = new TxReceipt[_blocks.Length][];
        for (int i = 0; i < decoded.Length; i++)
        {
            RlpReader reader = new(GetEncoded(i));
            decoded[i] = decodeBlock(ref reader);
            reader.CheckEnd();
        }

        return decoded;
    }

    public IEnumerator<TxReceipt[]> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _memory?.Dispose();
        _memory = null;
    }
}
