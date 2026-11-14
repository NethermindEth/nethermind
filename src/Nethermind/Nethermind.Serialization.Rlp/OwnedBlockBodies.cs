// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
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
    private byte[]?[]? _encodedOnRead;
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
        // Holds where each body content is in its block until the copy loop replaces it with the place of the copy.
        Range[] encodedBodies = new Range[blocks.Count];
        int length = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            encodedBodies[i] = GetBodyContent(blocks[i]);
            length += Rlp.LengthOfSequence(encodedBodies[i].GetOffsetAndLength(blocks[i].Length).Length);
        }

        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
        RlpWriter writer = RlpWriter.ContentWriter(memory.Memory.Span[..length]);
        for (int i = 0; i < blocks.Count; i++)
        {
            int start = writer.Length;
            ReadOnlySpan<byte> content = blocks[i].AsSpan(encodedBodies[i]);
            RlpSequence sequence = writer.StartSequence(content.Length);
            writer.WriteEncoded(content);
            writer.EndSequence(sequence);
            encodedBodies[i] = new Range(start, writer.Length);
        }

        return new OwnedBlockBodies(memory, encodedBodies, BlockBodyDecoder.Instance);

        static Range GetBodyContent(byte[] block)
        {
            RlpReader items = new RlpReader(block).EnterSequence(out _);
            items.SkipItem();
            int start = block.Length - items.Remaining;
            return new Range(start, start + items.ReadRaw(items.Remaining).Length);
        }
    }

    /// <summary>The bodies, decoded on the first read when they are held encoded.</summary>
    /// <remarks>
    /// A <see langword="null"/> body is one the peer did not have. The decoded bodies do not refer to the encoded
    /// memory, so the first read releases it.
    /// </remarks>
    /// <exception cref="RlpReaderException">An encoded body is not a valid body.</exception>
    /// <exception cref="ObjectDisposedException">The encoded bodies were released before the first read.</exception>
    public BlockBody?[] Bodies => _bodies ??= DecodeBodies();

    /// <summary>The encoded body at <paramref name="index"/> with its sequence prefix, encoded on the first call if it is held decoded.</summary>
    /// <remarks>
    /// An empty sequence is a body the peer did not have. The memory is valid until this owner is disposed or disowned,
    /// or until the first read of <see cref="Bodies"/>, the indexer or the enumerator releases the encoded bodies.
    /// </remarks>
    public Memory<byte> GetEncoded(int index)
    {
        if (_encodedBodies is not null && _memoryOwner is not null)
        {
            return _memoryOwner.Memory[_encodedBodies[index]];
        }

        _encodedOnRead ??= new byte[]?[_count];
        return _encodedOnRead[index] ??= Bodies[index] is { } body
            ? (_decoder ?? BlockBodyDecoder.Instance).Encode(body)
            : [Rlp.EmptyListByte];
    }

    /// <summary>
    /// The body at <paramref name="index"/> decoded by <see cref="BlockBodyDecoder.DecodeWithEncodedTransactions"/>,
    /// which keeps its transactions encoded. The transactions are valid until this owner is disposed or disowned, or
    /// until the first read of <see cref="Bodies"/>, the indexer or the enumerator releases the encoded bodies.
    /// </summary>
    /// <exception cref="RlpReaderException">The encoded body is not a valid body.</exception>
    public BlockBody? DecodeWithEncodedTransactions(int index) =>
        (_decoder ?? BlockBodyDecoder.Instance).DecodeWithEncodedTransactions(GetEncoded(index));

    private BlockBody?[] DecodeBodies()
    {
        IMemoryOwner<byte> memory = _memoryOwner ?? throw new ObjectDisposedException(nameof(OwnedBlockBodies));

        BlockBody?[] bodies = new BlockBody?[_count];
        _ownsPooledTransactions = true;
        try
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                RlpReader reader = new(memory.Memory.Span[_encodedBodies![i]]);
                bodies[i] = _decoder!.Decode(ref reader);
                reader.CheckEnd();
            }
        }
        catch
        {
            ReturnPooledTransactions(bodies);
            throw;
        }

        memory.Dispose();
        _memoryOwner = null;
        return bodies;
    }

    /// <summary>Gives the decoded bodies and their transactions to the caller, so that disposal does not return them to the pool.</summary>
    /// <remarks>Bodies not decoded yet by a read of <see cref="Bodies"/> are released.</remarks>
    public void Disown()
    {
        _ownsPooledTransactions = false;
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
