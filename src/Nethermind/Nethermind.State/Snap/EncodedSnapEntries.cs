// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.State.Snap;

/// <summary>Snap range entries held as their wire encoding in pooled memory.</summary>
/// <remarks>
/// A snap server fills it from trie leaves and the message serializer writes <see cref="Encoded"/> as it is, so the
/// served entries are not decoded to objects and encoded again. The first read of the entries decodes all of them.
/// </remarks>
/// <param name="decodeEntry">Decodes one entry and moves the reader past it.</param>
/// <param name="capacity">The length in bytes of the first buffer, so that the expected entries fit without a copy.</param>
public sealed class EncodedSnapEntries<T>(RefRlpReaderFunc<T> decodeEntry, int capacity = 0) : IOwnedReadOnlyList<T>
{
    private byte[] _buffer = [];
    private int _length;
    private T[]? _decoded;
    private readonly EncodedSnapEntries<T>? _owner;
    private readonly int _start;

    /// <summary>Creates a read-only view of <paramref name="count"/> entries in <paramref name="owner"/>, which holds their memory.</summary>
    /// <remarks>The entries are valid until <paramref name="owner"/> is disposed.</remarks>
    internal EncodedSnapEntries(RefRlpReaderFunc<T> decodeEntry, EncodedSnapEntries<T> owner, int start, int length, int count) : this(decodeEntry)
    {
        _owner = owner;
        _start = start;
        _length = length;
        Count = count;
    }

    /// <summary>The encoded entries one after the other, without the prefix of the list.</summary>
    public ReadOnlySpan<byte> Encoded => _owner is null ? _buffer.AsSpan(0, _length) : _owner.Encoded.Slice(_start, _length);

    public int Count { get; private set; }

    /// <summary>Adds an entry of <paramref name="length"/> bytes.</summary>
    /// <returns>The memory that the caller fills with the encoded entry.</returns>
    /// <exception cref="InvalidOperationException">These entries are a view of other entries.</exception>
    public Span<byte> Add(int length)
    {
        if (_owner is not null) throw new InvalidOperationException("A view of encoded snap entries is read-only.");

        int newLength = _length + length;
        if (newLength > _buffer.Length)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(newLength, Math.Max(_buffer.Length * 2, capacity)));
            Encoded.CopyTo(buffer);
            ReturnBuffer();
            _buffer = buffer;
        }

        Span<byte> entry = _buffer.AsSpan(_length, length);
        _length = newLength;
        _decoded = null;
        Count++;
        return entry;
    }

    public T this[int index] => AsSpan()[index];

    public ReadOnlySpan<T> AsSpan() => _decoded ??= Decode();

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_decoded ??= Decode())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private T[] Decode()
    {
        T[] entries = new T[Count];
        RlpReader reader = new(Encoded);
        for (int i = 0; i < entries.Length; i++) entries[i] = decodeEntry(ref reader);
        return entries;
    }

    public void Dispose()
    {
        ReturnBuffer();
        _buffer = [];
        _length = 0;
        _decoded = null;
        Count = 0;
    }

    private void ReturnBuffer()
    {
        if (_buffer.Length != 0) ArrayPool<byte>.Shared.Return(_buffer);
    }
}
