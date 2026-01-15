// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// An IRlpReader implementation that counts elements instead of decoding them.
/// Used for pre-validation to prevent memory DOS attacks by scanning the RLP
/// structure before allocating any memory.
///
/// The counting reader wraps an RlpStream and uses its scanning capabilities
/// to traverse the RLP data without performing actual allocations.
/// </summary>
public sealed class CountingRlpReader : IRlpReader
{
    private readonly RlpStream _inner;
    private int _currentDepth;

    /// <summary>
    /// Total number of collection elements counted during validation.
    /// </summary>
    public int TotalElementsCounted { get; private set; }

    /// <summary>
    /// Maximum total elements allowed across all collections.
    /// </summary>
    public int MaxElementsAllowed { get; set; } = 1_000_000;

    /// <summary>
    /// Maximum nesting depth allowed.
    /// </summary>
    public int MaxDepth { get; set; } = 16;

    /// <summary>
    /// Creates a counting reader that wraps the specified RLP stream.
    /// </summary>
    public CountingRlpReader(RlpStream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public int Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public int Length => _inner.Length;

    /// <inheritdoc />
    public int ReadSequenceLength() => _inner.ReadSequenceLength();

    /// <inheritdoc />
    public void SkipItem() => _inner.SkipItem();

    /// <inheritdoc />
    public byte PeekByte() => _inner.PeekByte();

    /// <inheritdoc />
    public int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
        => _inner.PeekNumberOfItemsRemaining(beforePosition, maxSearch);

    // Primitive decodes - skip the item and return default values
    // These don't allocate significant memory so we just skip them

    /// <inheritdoc />
    public long DecodeLong()
    {
        _inner.SkipItem();
        return 0;
    }

    /// <inheritdoc />
    public ulong DecodeULong()
    {
        _inner.SkipItem();
        return 0;
    }

    /// <inheritdoc />
    public int DecodeInt()
    {
        _inner.SkipItem();
        return 0;
    }

    /// <inheritdoc />
    public byte DecodeByte()
    {
        _inner.SkipItem();
        return 0;
    }

    /// <inheritdoc />
    public byte[] DecodeByteArray(RlpLimit? limit = null)
    {
        _inner.SkipItem();
        return [];
    }

    /// <inheritdoc />
    public ReadOnlySpan<byte> DecodeByteArraySpan(RlpLimit? limit = null)
    {
        _inner.SkipItem();
        return [];
    }

    /// <inheritdoc />
    public Hash256? DecodeKeccak()
    {
        _inner.SkipItem();
        return null;
    }

    /// <inheritdoc />
    public Address? DecodeAddress()
    {
        _inner.SkipItem();
        return null;
    }

    /// <inheritdoc />
    public UInt256 DecodeUInt256(int length = -1)
    {
        _inner.SkipItem();
        return UInt256.Zero;
    }

    /// <inheritdoc />
    public T[] DecodeArray<T>(
        Func<IRlpReader, T> decodeItem,
        bool checkPositions = true,
        T defaultElement = default,
        RlpLimit? limit = null)
    {
        DecodeCollectionCounting(decodeItem, limit);
        return [];
    }

    /// <inheritdoc />
    public ArrayPoolList<T> DecodeArrayPoolList<T>(
        Func<IRlpReader, T> decodeItem,
        bool checkPositions = true,
        T defaultElement = default,
        RlpLimit? limit = null)
    {
        DecodeCollectionCounting(decodeItem, limit);
        return new ArrayPoolList<T>(0);
    }

    private void DecodeCollectionCounting<T>(Func<IRlpReader, T> decodeItem, RlpLimit? limit)
    {
        int sequenceLength = _inner.ReadSequenceLength();
        int endPosition = _inner.Position + sequenceLength;
        int elementCount = _inner.PeekNumberOfItemsRemaining(endPosition);

        // Validate against total limit
        TotalElementsCounted += elementCount;
        if (TotalElementsCounted > MaxElementsAllowed)
        {
            ThrowTotalLimitExceeded();
        }

        // Validate against per-collection limit if specified
        if (limit.HasValue && elementCount > limit.Value.Limit)
        {
            ThrowLimitExceeded(limit.Value, elementCount);
        }

        // Track nesting depth
        _currentDepth++;
        if (_currentDepth > MaxDepth)
        {
            ThrowMaxDepthExceeded();
        }

        // Recursively validate nested structures by calling decodeItem
        // The callback will call back into this CountingRlpReader for nested collections
        while (_inner.Position < endPosition)
        {
            // Check for empty sequence marker (0xC0)
            if (_inner.PeekByte() == Rlp.OfEmptySequence[0])
            {
                _inner.Position++;
            }
            else
            {
                // This recursively counts nested structures
                decodeItem(this);
            }
        }

        _currentDepth--;
    }

    [DoesNotReturn]
    private void ThrowTotalLimitExceeded()
    {
        throw new RlpLimitException(
            $"Total elements {TotalElementsCounted} exceeds limit {MaxElementsAllowed}");
    }

    [DoesNotReturn]
    private static void ThrowLimitExceeded(RlpLimit limit, int count)
    {
        string name = string.IsNullOrEmpty(limit.CollectionExpression)
            ? "Collection"
            : limit.CollectionExpression;
        throw new RlpLimitException($"{name} count {count} exceeds limit {limit.Limit}");
    }

    [DoesNotReturn]
    private void ThrowMaxDepthExceeded()
    {
        throw new RlpLimitException($"Nesting depth {_currentDepth} exceeds limit {MaxDepth}");
    }
}
