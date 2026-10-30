// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

/// <summary>Accumulates the content length of one RLP container in a <see cref="LengthList"/>.</summary>
/// <remarks>
/// Follows <see cref="DeferredRlpItemList.Builder.Writer"/>: <see cref="BeginContainer"/> reserves the child's slot
/// before anything nested in it, so the list holds the lengths in encode order,
/// and <see cref="Dispose"/> adds the child's sequence length to its parent.
/// </remarks>
public readonly ref struct LengthListWriter : IDisposable
{
    private readonly Span<int> _initialBuffer;
    private readonly ref LengthListStorage _storage;
    private readonly int _index;
    private readonly int _parentIndex;

    internal LengthListWriter(Span<int> initialBuffer, ref LengthListStorage storage, int index, int parentIndex)
    {
        _initialBuffer = initialBuffer;
        _storage = ref storage;
        _index = index;
        _parentIndex = parentIndex;
    }

    /// <summary>The content length added so far.</summary>
    public int Length => _storage.Lengths(_initialBuffer)[_index];

    /// <summary>Adds the encoded length of an item that takes no slot of its own.</summary>
    public void Add(int length) => _storage.Lengths(_initialBuffer)[_index] += length;

    public LengthListWriter BeginContainer() => new(_initialBuffer, ref _storage, _storage.Reserve(_initialBuffer), _index);

    public void Dispose()
    {
        if (_parentIndex < 0) return;
        Span<int> lengths = _storage.Lengths(_initialBuffer);
        lengths[_parentIndex] += Rlp.LengthOfSequence(lengths[_index]);
    }
}
