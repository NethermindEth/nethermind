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
/// Every receipt that <see cref="TryCreate"/> takes is one <see cref="NetworkReceipt.TryRead"/> reads, so a consumer that
/// stores received receipts without using them can read <see cref="GetEncoded"/> and skip decoding the logs.
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
    // Holds null for the blocks not decoded yet.
    private TxReceipt[][]? _decoded;

    /// <summary>Whether the receipts have blooms: the eth/63 to eth/68 form.</summary>
    public bool WithBloom { get; } = withBloom;

    /// <summary>Decodes the encoded receipt sequence of one block, as reads of this list decode it.</summary>
    public RefRlpReaderFunc<TxReceipt[]> BlockDecoder { get; } = decodeBlock;

    public int Count => _blocks.Length;

    /// <summary>The encoded receipt sequence of the block at <paramref name="index"/>, valid until disposal.</summary>
    public ReadOnlyMemory<byte> GetEncoded(int index) =>
        (_memory ?? throw new ObjectDisposedException(nameof(NetworkBlockReceipts))).Memory[_blocks[index]];

    /// <summary>Takes the receipts of each block of <paramref name="encoded"/> when every receipt is one <see cref="NetworkReceipt.TryRead"/> reads.</summary>
    /// <param name="encoded">The encoded sequence of the blocks' receipt sequences.</param>
    /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
    /// <param name="decodeBlock">Decodes the receipt sequence of one block.</param>
    /// <param name="allowZeroLengthBloom">Whether <paramref name="decodeBlock"/> takes a zero-length bloom (EIP-7668).</param>
    /// <returns>The receipts, or <see langword="null"/> when a receipt has a shape left to the receipt decoder.</returns>
    /// <exception cref="RlpReaderException">The receipts are not valid RLP.</exception>
    public static NetworkBlockReceipts? TryCreate(ReadOnlySpan<byte> encoded, bool withBloom, RefRlpReaderFunc<TxReceipt[]> decodeBlock, RlpLimit blocksLimit, RlpLimit receiptsLimit, bool allowZeroLengthBloom)
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
                if (!NetworkReceipt.TryRead(ref receipts, withBloom, out NetworkReceipt receipt)
                    || (withBloom && !allowZeroLengthBloom && receipt.Bloom.IsEmpty)) return null;
            }

            ranges[i] = new Range(start, start + block.Length);
        }

        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(encoded.Length);
        encoded.CopyTo(memory.Memory.Span);
        return new NetworkBlockReceipts(memory, ranges, withBloom, decodeBlock);
    }

    /// <summary>Takes the encoded receipt sequences of blocks from <paramref name="buffer"/>, which it then owns.</summary>
    /// <param name="buffer">The blocks' encoded receipt sequences, each as the receipts message carries it.</param>
    /// <param name="blocks">Where the receipt sequence of each block is in <paramref name="buffer"/>.</param>
    /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
    /// <param name="decodeBlock">Decodes the receipt sequence of one block.</param>
    public static NetworkBlockReceipts FromBuffer(ArrayPoolList<byte> buffer, Range[] blocks, bool withBloom, RefRlpReaderFunc<TxReceipt[]> decodeBlock) =>
        new(new PooledListMemory(buffer), blocks, withBloom, decodeBlock);

    private sealed class PooledListMemory(ArrayPoolList<byte> list) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => list.AsMemory();

        public void Dispose() => list.Dispose();
    }

    /// <summary>The receipts of the block at <paramref name="index"/>, decoded on the first read of that block only.</summary>
    /// <exception cref="RlpReaderException">The block's receipts are not valid.</exception>
    public TxReceipt[] this[int index]
    {
        get
        {
            _decoded ??= new TxReceipt[_blocks.Length][];
            TxReceipt[]? receipts = _decoded[index];
            return receipts ?? (_decoded[index] = Decode(index));
        }
    }

    /// <summary>The receipts of every block, each decoded on its first read.</summary>
    public ReadOnlySpan<TxReceipt[]> AsSpan()
    {
        for (int i = 0; i < _blocks.Length; i++) _ = this[i];
        return _decoded ?? [];
    }

    private TxReceipt[] Decode(int index)
    {
        RlpReader reader = new(GetEncoded(index).Span);
        TxReceipt[] receipts = BlockDecoder(ref reader);
        reader.CheckEnd();
        return receipts;
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
