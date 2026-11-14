// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// A holder for BlockBody[] that must be explicitly disposed or there will be memory leak. May uses netty's buffer directly.
/// BlockBody may contain `Memory<byte>` that is explicitly managed. Reusing `BlockBody` from this object after Dispose
/// is likely to cause corrupted `BlockBody`.
/// </summary>
/// <remarks>
/// Bodies from the network stay encoded in pooled memory and are decoded on the first read of <see cref="Bodies"/>,
/// so a consumer that stores bodies without executing them can use <see cref="GetEncoded"/> and skip the decode.
/// </remarks>
public class OwnedBlockBodies : IDisposable, IReadOnlyList<BlockBody?>
{
    private BlockBody?[]? _bodies;
    private readonly int _count;

    private IMemoryOwner<byte>? _memoryOwner;
    private readonly Range[]? _encodedBodies;
    private readonly BlockBodyDecoder? _decoder;
    private bool _ownsPooledTransactions;

    /// <param name="bodies">Bodies whose lifetimes are managed by this owner.</param>
    /// <param name="ownsPooledTransactions">Whether disposal returns the exclusively owned transactions to the pool.</param>
    public OwnedBlockBodies(BlockBody?[] bodies, bool ownsPooledTransactions = false)
    {
        _bodies = bodies;
        _count = bodies.Length;
        _ownsPooledTransactions = ownsPooledTransactions;
    }

    /// <param name="memoryOwner">Memory that holds the encoded bodies, released on disposal or disowning.</param>
    /// <param name="encodedBodies">Where each encoded body, with its sequence prefix, is in <paramref name="memoryOwner"/>.</param>
    /// <param name="decoder">Decodes the bodies when <see cref="Bodies"/> is read.</param>
    public OwnedBlockBodies(IMemoryOwner<byte> memoryOwner, Range[] encodedBodies, BlockBodyDecoder decoder)
    {
        _memoryOwner = memoryOwner;
        _encodedBodies = encodedBodies;
        _count = encodedBodies.Length;
        _decoder = decoder;
    }

    /// <summary>Takes the bodies of encoded blocks without decoding them.</summary>
    /// <param name="blocks">Encoded blocks, each a sequence of the header and then the body items.</param>
    /// <exception cref="RlpReaderException">A block is not a sequence that starts with an item.</exception>
    public static OwnedBlockBodies FromEncodedBlocks(IReadOnlyList<byte[]> blocks)
    {
        int length = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            length += Rlp.LengthOfSequence(GetBodyContent(blocks[i]).Length);
        }

        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
        Range[] encodedBodies = new Range[blocks.Count];
        RlpWriter writer = RlpWriter.ContentWriter(memory.Memory.Span[..length]);
        for (int i = 0; i < blocks.Count; i++)
        {
            int start = writer.Length;
            ReadOnlySpan<byte> content = GetBodyContent(blocks[i]);
            RlpSequence sequence = writer.StartSequence(content.Length);
            writer.WriteEncoded(content);
            writer.EndSequence(sequence);
            encodedBodies[i] = new Range(start, writer.Length);
        }

        return new OwnedBlockBodies(memory, encodedBodies, BlockBodyDecoder.Instance);

        static ReadOnlySpan<byte> GetBodyContent(byte[] block)
        {
            RlpReader items = new RlpReader(block).EnterSequence(out _);
            items.SkipItem();
            return items.ReadRaw(items.Remaining);
        }
    }

    /// <summary>The bodies, decoded on the first read when they are held encoded.</summary>
    /// <remarks>A <see langword="null"/> body is one the peer did not have.</remarks>
    /// <exception cref="RlpReaderException">An encoded body is not a valid body.</exception>
    public BlockBody?[] Bodies => _bodies ??= DecodeBodies();

    /// <summary>The encoded body at <paramref name="index"/> with its sequence prefix, encoded now if it is held decoded.</summary>
    /// <remarks>
    /// An empty sequence is a body the peer did not have. The memory is valid until this owner is disposed or disowned.
    /// </remarks>
    public Memory<byte> GetEncoded(int index)
    {
        if (_encodedBodies is not null && _memoryOwner is not null)
        {
            return _memoryOwner.Memory[_encodedBodies[index]];
        }

        BlockBody? body = Bodies[index];
        return body is null ? new[] { Rlp.EmptyListByte } : (_decoder ?? BlockBodyDecoder.Instance).Encode(body);
    }

    /// <summary>
    /// The body at <paramref name="index"/> decoded by <see cref="BlockBodyDecoder.DecodeWithEncodedTransactions"/>,
    /// which keeps its transactions encoded. The transactions are valid until this owner is disposed or disowned.
    /// </summary>
    /// <exception cref="RlpReaderException">The encoded body is not a valid body.</exception>
    public BlockBody? DecodeWithEncodedTransactions(int index) =>
        (_decoder ?? BlockBodyDecoder.Instance).DecodeWithEncodedTransactions(GetEncoded(index));

    private BlockBody?[] DecodeBodies()
    {
        if (_encodedBodies is null || _memoryOwner is null || _decoder is null) return [];

        BlockBody?[] bodies = new BlockBody?[_encodedBodies.Length];
        _ownsPooledTransactions = true;
        try
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                RlpReader reader = new(_memoryOwner.Memory.Span[_encodedBodies[i]]);
                bodies[i] = _decoder.Decode(ref reader);
                reader.CheckEnd();
            }
        }
        catch
        {
            ReturnPooledTransactions(bodies);
            throw;
        }

        return bodies;
    }

    /// <summary>
    /// Disown the `BlockBody`, copying any `Memory<byte>` so that it does not depend on the `_memoryOwner.`
    /// </summary>
    /// <remarks>Bodies not decoded yet by a read of <see cref="Bodies"/> are released.</remarks>
    public void Disown()
    {
        _ownsPooledTransactions = false;
        if (_memoryOwner is null) return;

        foreach (BlockBody? blockBody in _bodies ?? [])
        {
            if (blockBody is null) continue;
            foreach (Transaction tx in blockBody.Transactions)
            {
                Hash256? _ = tx.Hash; // Just need to trigger hash calculation
                // Disconnect from any backing
                tx.Data = tx.Data.ToArray();
            }
        }

        _memoryOwner?.Dispose();
        _memoryOwner = null;
    }

    public void Dispose()
    {
        if (_ownsPooledTransactions)
        {
            _ownsPooledTransactions = false;
            if (_bodies is not null) ReturnPooledTransactions(_bodies);
        }

        _memoryOwner?.Dispose();
        _memoryOwner = null;
    }

    private static void ReturnPooledTransactions(BlockBody?[] bodies)
    {
        foreach (BlockBody? blockBody in bodies)
        {
            if (blockBody is null) continue;
            foreach (Transaction tx in blockBody.Transactions)
            {
                TxDecoder.TxObjectPool.Return(tx);
            }
        }
    }

    public IEnumerator<BlockBody?> GetEnumerator()
    {
        foreach (BlockBody? blockBody in Bodies)
        {
            yield return blockBody;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => Bodies.GetEnumerator();

    public int Count => _count;

    public BlockBody? this[int index] => Bodies[index];
}
