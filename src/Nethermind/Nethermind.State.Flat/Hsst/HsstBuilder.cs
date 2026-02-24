// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries.
/// Entries MUST be added in sorted key order. No internal sorting is performed.
///
/// Binary layout (normal):
///   [Version: u8 = 0x01][Data Region: entries...][Index Region: B-tree nodes...]
///   Root index is readable from the end via MetadataLength byte (no trailer).
///
/// Binary layout (inline):
///   [Version: u8 = 0x81][Index Region: B-tree nodes...]
///   No data section. Leaf values are stored directly in the B-tree index.
///
/// Entry format (normal, value first, lengths forward-readable from MetadataStart):
///   [Value][ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey]
/// </summary>
public ref struct HsstBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private int _writtenBeforeValue;
    private readonly int _baseOffset;

    private readonly int _minSeparatorLength;
    private readonly bool _inlineValues;

    // Working buffers allocated from ArrayPool
    private ArrayPoolListRef<byte> _separatorBuffer;
    private ArrayPoolListRef<HsstEntry> _entriesBuffer;
    private ArrayPoolListRef<byte> _prevKeyBuffer;

    // Inline value buffers (only allocated when _inlineValues is true)
    private ArrayPoolListRef<byte> _inlineValueBuffer;
    private ArrayPoolListRef<int> _inlineValueLengths;

    public readonly struct HsstEntry(int sepOffset, int sepLen, int metadataStart)
    {
        public readonly int SepOffset = sepOffset;
        public readonly int SepLen = sepLen;
        /// <summary>
        /// Normal: offset relative to position 1 (after version byte) where value metadata starts.
        /// Inline: offset into the inline value buffer.
        /// </summary>
        public readonly int MetadataStart = metadataStart;
    }

    /// <summary>
    /// Create builder writing via the given writer.
    /// Writes version byte (0x01 normal, 0x81 inline).
    /// Allocates working buffers from ArrayPool — call Dispose() to return them.
    /// </summary>
    public HsstBuilder(ref TWriter writer, int minSeparatorLength = 0, bool inlineValues = false)
    {
        _writer = ref writer;
        _baseOffset = _writer.Written;
        _minSeparatorLength = minSeparatorLength;
        _inlineValues = inlineValues;
        _separatorBuffer = new ArrayPoolListRef<byte>(65536);
        _entriesBuffer = new ArrayPoolListRef<HsstEntry>(10000);
        _prevKeyBuffer = new ArrayPoolListRef<byte>(256);

        if (inlineValues)
        {
            _inlineValueBuffer = new ArrayPoolListRef<byte>(65536);
            _inlineValueLengths = new ArrayPoolListRef<int>(10000);
        }

        // Write version byte
        Span<byte> span = _writer.GetSpan(1);
        span[0] = inlineValues ? (byte)0x81 : (byte)0x01;
        _writer.Advance(1);
    }

    /// <summary>
    /// Return pooled buffers to ArrayPool.
    /// </summary>
    public void Dispose()
    {
        _separatorBuffer.Dispose();
        _entriesBuffer.Dispose();
        _prevKeyBuffer.Dispose();
        if (_inlineValues)
        {
            _inlineValueBuffer.Dispose();
            _inlineValueLengths.Dispose();
        }
    }

    /// <summary>
    /// Begin writing a value. Returns ref to the shared writer and snapshots Written.
    /// After writing, call FinishValueWrite with just the key.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        if (_inlineValues) throw new NotSupportedException("BeginValueWrite not supported in inline mode. Use Add() instead.");
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write. Computes length from snapshot taken by BeginValueWrite.
    /// Key must be greater than previous key (sorted order).
    /// </summary>
    public void FinishValueWrite(ReadOnlySpan<byte> key)
    {
        if (_inlineValues) throw new NotSupportedException("FinishValueWrite not supported in inline mode. Use Add() instead.");

        int actualLen = _writer.Written - _writtenBeforeValue;
        // metadataStart stored in index is relative to position 1 (after this builder's version byte)
        int metadataStart = _writer.Written - _baseOffset - 1;

        // Compute separator eagerly
        int sepLen = ComputeSeparatorLength(
            _prevKeyBuffer.AsSpan(),
            key,
            nextKey: default,
            _minSeparatorLength);

        int sepOffset = _separatorBuffer.Count;
        _separatorBuffer.AddRange(key[..sepLen]);

        ReadOnlySpan<byte> remainingKey = key[sepLen..];

        // Write [ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey]
        Span<byte> leb = _writer.GetSpan(10);
        int lebLen = Leb128.Write(leb, 0, actualLen);
        _writer.Advance(lebLen);

        leb = _writer.GetSpan(10);
        lebLen = Leb128.Write(leb, 0, remainingKey.Length);
        _writer.Advance(lebLen);

        if (remainingKey.Length > 0)
        {
            remainingKey.CopyTo(_writer.GetSpan(remainingKey.Length));
            _writer.Advance(remainingKey.Length);
        }

        _entriesBuffer.Add(new HsstEntry(sepOffset, sepLen, metadataStart));

        _prevKeyBuffer.Clear();
        _prevKeyBuffer.AddRange(key);
    }

    /// <summary>
    /// Convenience: add key-value pair in one call.
    /// </summary>
    public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (_inlineValues)
        {
            // Inline: separator = full key, buffer value separately
            int sepOffset = _separatorBuffer.Count;
            _separatorBuffer.AddRange(key);

            int valueOffset = _inlineValueBuffer.Count;
            _inlineValueBuffer.AddRange(value);
            _inlineValueLengths.Add(value.Length);

            _entriesBuffer.Add(new HsstEntry(sepOffset, key.Length, valueOffset));

            _prevKeyBuffer.Clear();
            _prevKeyBuffer.AddRange(key);
        }
        else
        {
            _writtenBeforeValue = _writer.Written;
            value.CopyTo(_writer.GetSpan(value.Length));
            _writer.Advance(value.Length);
            FinishValueWrite(key);
        }
    }

    /// <summary>
    /// Build index. The ref writer is already advanced.
    /// No trailer is written — the root index is readable from the end.
    /// </summary>
    public void Build(int maxLeafEntries = Hsst.MaxLeafEntries)
    {
        if (_inlineValues)
        {
            // Inline: no data section, index starts right after version byte
            int absoluteIndexStart = 1;

            HsstIndexBuilder<TWriter> indexBuilder = new(
                ref _writer, _entriesBuffer.AsSpan(),
                _separatorBuffer.AsSpan(),
                _inlineValueBuffer.AsSpan(),
                _inlineValueLengths.AsSpan());

            indexBuilder.Build(absoluteIndexStart, maxLeafEntries);
        }
        else
        {
            int absoluteIndexStart = _writer.Written - _baseOffset;

            HsstIndexBuilder<TWriter> indexBuilder = new(
                ref _writer, _entriesBuffer.AsSpan(),
                _separatorBuffer.AsSpan());

            indexBuilder.Build(absoluteIndexStart, maxLeafEntries);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeSeparatorLength(ReadOnlySpan<byte> prevKey, ReadOnlySpan<byte> currKey, ReadOnlySpan<byte> nextKey, int minSeparatorLength = 0)
    {
        int minVsPrev = 0;
        if (!prevKey.IsEmpty)
        {
            int common = CommonPrefixLength(prevKey, currKey);
            minVsPrev = common + 1;
        }

        int minVsNext = 0;
        if (!nextKey.IsEmpty)
        {
            int common = CommonPrefixLength(currKey, nextKey);
            minVsNext = common + 1;
        }

        int len = Math.Max(minVsPrev, minVsNext);
        len = Math.Min(len, currKey.Length);
        if (len == 0) len = Math.Min(1, currKey.Length);

        return Math.Min(Math.Max(len, minSeparatorLength), currKey.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return minLen;
    }
}
