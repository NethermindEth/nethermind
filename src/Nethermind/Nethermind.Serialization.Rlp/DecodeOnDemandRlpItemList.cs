// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public abstract class DecodeOnDemandRlpItemList<T>(RlpItemList inner) : IOwnedReadOnlyList<T>, IRlpWrapper
{
    private int _cachedIndex = -1;
    private T? _cachedItem;

    protected RlpItemList Inner => inner;

    public int Count => inner.Count;

    public T this[int index]
    {
        get
        {
            if (_cachedIndex == index)
                return _cachedItem!;

            T item = DecodeItem(index);
            _cachedIndex = index;
            _cachedItem = item;
            return item;
        }
    }

    protected abstract T DecodeItem(int index);

    public ReadOnlySpan<T> AsSpan() => throw new NotSupportedException();

    public ReadOnlySpan<byte> RlpSpan => inner.RlpSpan;

    public void Dispose() => inner.Dispose();

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
