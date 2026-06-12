// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed class Distances : IReadOnlyList<int>, IDisposable
{
    internal const int MaxCount = 257;
    private const int InlineCapacity = 3;

    private int[]? _rented;
    private int _first;
    private int _second;
    private int _third;

    public Distances(ReadOnlySpan<int> distances)
        : this(distances.Length)
    {
        for (int i = 0; i < distances.Length; i++)
        {
            Set(i, distances[i]);
        }
    }

    internal Distances(int count)
    {
        if ((uint)count > MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Distance count must be between 0 and {MaxCount}.");
        }

        Count = count;
        if (count > InlineCapacity)
        {
            _rented = SafeArrayPool<int>.Shared.Rent(count);
        }
    }

    public int Count { get; }

    public int this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count, nameof(index));

            if (_rented is not null)
            {
                return _rented[index];
            }

            return index switch
            {
                0 => _first,
                1 => _second,
                2 => _third,
                _ => throw new ArgumentOutOfRangeException(nameof(index))
            };
        }
    }

    public void Dispose()
    {
        if (_rented is not null)
        {
            SafeArrayPool<int>.Shared.Return(_rented);
            _rented = null;
        }
    }

    public IEnumerator<int> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Set(int index, int value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count, nameof(index));

        if (_rented is not null)
        {
            _rented[index] = value;
            return;
        }

        switch (index)
        {
            case 0:
                _first = value;
                return;
            case 1:
                _second = value;
                return;
            case 2:
                _third = value;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
