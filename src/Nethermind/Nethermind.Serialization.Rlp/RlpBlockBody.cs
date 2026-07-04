// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// RLP-backed view of a single block body that defers transaction decoding until needed.
/// </summary>
/// <remarks>
/// Stores the body content region <c>[txs-seq][uncles-seq][withdrawals-seq?]</c> without the enclosing list
/// prefix, which makes a wire body item and the tail of a stored block interchangeable sources.
/// Construction validates the top-level structure (2 or 3 sub-lists exactly filling the region) so garbage
/// nesting still fails at deserialize time; the sub-lists' contents are only validated by <see cref="Decode"/>.
/// The instance owns its memory and must be disposed exactly once. <see cref="Decode"/> returns pooled,
/// buffer-backed transactions that are valid only until disposal unless <see cref="DetachDecoded"/> was called.
/// Not thread-safe.
/// </remarks>
public sealed class RlpBlockBody : IDisposable, IRlpWrapper
{
    private readonly IMemoryOwner<byte> _memoryOwner;
    private readonly Memory<byte> _content;
    private readonly int _txsLength;
    private readonly int _unclesLength;
    private readonly bool _hasWithdrawals;

    private BlockBody? _decoded;
    private bool _detached;
    private bool _disposed;

    private RlpBlockBody(IMemoryOwner<byte> memoryOwner, Memory<byte> content)
    {
        _memoryOwner = memoryOwner;
        _content = content;

        ReadOnlySpan<byte> span = content.Span;
        Span<int> itemLengths = stackalloc int[3];
        int itemCount = 0;
        int position = 0;
        try
        {
            while (position < span.Length)
            {
                if (itemCount == 3 || span[position] < 0xc0)
                {
                    ThrowInvalidStructure();
                }

                (int prefixLength, int contentLength) = RlpHelpers.PeekPrefixAndContentLength(span, position);
                itemLengths[itemCount++] = prefixLength + contentLength;
                position += prefixLength + contentLength;
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // A nested length prefix pointing past the region surfaces as an out-of-range access.
            ThrowInvalidStructure();
        }

        if (itemCount < 2 || position != span.Length)
        {
            ThrowInvalidStructure();
        }

        _txsLength = itemLengths[0];
        _unclesLength = itemLengths[1];
        _hasWithdrawals = itemCount == 3;

        static void ThrowInvalidStructure() =>
            throw new RlpException($"{nameof(BlockBody)} must be an RLP list of 2 or 3 lists");
    }

    /// <summary>Wraps a wire body item, i.e. the RLP list <c>[[txs],[uncles],(withdrawals)?]</c> including its prefix.</summary>
    /// <remarks>Empty-list items (null-body placeholders) are rejected; the caller maps them to null slots.</remarks>
    public static RlpBlockBody FromBodyItem(IMemoryOwner<byte> memoryOwner, Memory<byte> bodyItemRlp)
    {
        if (bodyItemRlp.Length == 0 || bodyItemRlp.Span[0] < 0xc0)
        {
            throw new RlpException($"{nameof(BlockBody)} must be an RLP list");
        }

        (int prefixLength, int contentLength) = RlpHelpers.PeekPrefixAndContentLength(bodyItemRlp.Span, 0);
        if (contentLength == 0 || prefixLength + contentLength != bodyItemRlp.Length)
        {
            throw new RlpException($"Invalid {nameof(BlockBody)} item length");
        }

        return new RlpBlockBody(memoryOwner, bodyItemRlp.Slice(prefixLength, contentLength));
    }

    /// <summary>Slices the body out of a stored full-block RLP <c>[header,[txs],[uncles],[withdrawals]?]</c>.</summary>
    public static RlpBlockBody FromStoredBlock(IMemoryOwner<byte> memoryOwner, Memory<byte> blockRlp)
    {
        RlpReader ctx = new(blockRlp.Span);
        int contentLength = ctx.ReadSequenceLength();
        int end = ctx.Position + contentLength;
        ctx.SkipItem(); // header
        return new RlpBlockBody(memoryOwner, blockRlp[ctx.Position..end]);
    }

    /// <summary>Encodes an already-decoded body into a pooled buffer owned by the returned instance.</summary>
    /// <remarks>The body is kept as the decoded view but is treated as caller-owned: disposal never returns its transactions to the pool.</remarks>
    public static RlpBlockBody FromBody(BlockBody body)
    {
        BlockBodyDecoder decoder = BlockBodyDecoder.Instance;
        int length = decoder.GetLength(body, RlpBehaviors.None);
        IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(length);
        try
        {
            Memory<byte> memory = memoryOwner.Memory[..length];
            RlpWriter writer = new(memory.Span);
            decoder.Encode(ref writer, body);
            RlpBlockBody rlpBody = FromBodyItem(memoryOwner, memory);
            rlpBody._decoded = body;
            rlpBody._detached = true;
            return rlpBody;
        }
        catch
        {
            memoryOwner.Dispose();
            throw;
        }
    }

    /// <summary>The transactions sequence <c>[txs]</c> including its list prefix.</summary>
    public ReadOnlyMemory<byte> TransactionsSequence => _content[.._txsLength];

    /// <summary>The uncles sequence <c>[uncles]</c> including its list prefix.</summary>
    public ReadOnlyMemory<byte> UnclesSequence => _content.Slice(_txsLength, _unclesLength);

    /// <summary>The withdrawals sequence <c>[withdrawals]</c> including its list prefix, or null when absent.</summary>
    public ReadOnlyMemory<byte>? WithdrawalsSequence =>
        // The cast matters: an untyped null would silently convert to an empty Memory via the byte[] operator.
        _hasWithdrawals ? (ReadOnlyMemory<byte>?)_content[(_txsLength + _unclesLength)..] : null;

    /// <summary>The content region <c>[txs-seq][uncles-seq][withdrawals-seq?]</c> without the enclosing list prefix.</summary>
    public ReadOnlySpan<byte> RlpContentSpan => _content.Span;

    public int RlpContentLength => _content.Length;

    public int RlpLength => Rlp.LengthOfSequence(_content.Length);

    public void Write<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(_content.Length);
        writer.Write(_content.Span);
    }

    /// <summary>Decodes the body, caching the result; repeated calls return the same instance.</summary>
    /// <exception cref="RlpException">The raw bytes do not decode to a valid body.</exception>
    public BlockBody Decode()
    {
        if (_decoded is not null) return _decoded;

        RlpReader ctx = new(_content);
        BlockBody body = BlockBodyDecoder.Instance.DecodeUnwrapped(ref ctx, _content.Length)!;
        ctx.Check(_content.Length);
        return _decoded = body;
    }

    /// <summary>
    /// Decodes the body and disconnects it from the backing buffer: transaction hashes are finalized and
    /// calldata is copied out, so the body stays valid after disposal.
    /// </summary>
    public BlockBody DetachDecoded()
    {
        BlockBody body = Decode();
        if (!_detached)
        {
            foreach (Transaction tx in body.Transactions)
            {
                _ = tx.Hash; // Finalize the hash before the backing buffer is released
                tx.Data = tx.Data.ToArray();
            }

            _detached = true;
        }

        return body;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_decoded is not null && !_detached)
        {
            foreach (Transaction tx in _decoded.Transactions)
            {
                TxDecoder.TxObjectPool.Return(tx);
            }
        }

        _memoryOwner.Dispose();
    }
}
