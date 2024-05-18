// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public class MemoryContentsComparer<T> : IEqualityComparer<ReadOnlyMemory<T>>, IEqualityComparer<Memory<T>>
{
    public bool Equals(ReadOnlyMemory<T> x, ReadOnlyMemory<T> y) => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<T> obj)
    {
        int hc = 0;
        foreach (T p in obj.Span)
        {
            hc ^= p?.GetHashCode() ?? 0;
            hc = (hc << 7) | (hc >> (32 - 7));
        }

        return hc;
    }

    public bool Equals(Memory<T> x, Memory<T> y) => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(Memory<T> obj) => GetHashCode((ReadOnlyMemory<T>)obj);
}
