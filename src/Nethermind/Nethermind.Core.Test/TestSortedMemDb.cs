// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Nethermind.Db;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Core.Test;

/// <summary>
/// MemDB with additional tools for testing purposes since you can't use NSubstitute with refstruct
/// </summary>
public class TestSortedMemDb : MemDb, ISortedKeyValueStore
{
    private SortedSet<byte[]> _sortedKeys = new SortedSet<byte[]>(Bytes.Comparer);

    public byte[]? FirstKey
    {
        get
        {
            if (_sortedKeys.Count == 0) return null;
            return _sortedKeys.First();
        }
    }

    public byte[]? LastKey
    {
        get
        {
            if (_sortedKeys.Count == 0) return null;
            return _sortedKeys.Last();
        }
    }

    public ISortedView GetViewBetween(byte[] firstKey, byte[] lastKey)
    {
        return new SortedView(_sortedKeys.GetViewBetween(firstKey, lastKey).GetEnumerator(), this);
    }

    public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        base.Set(key, value, flags);
        if (value is null)
        {
            _sortedKeys.Remove(key.ToArray());
        }
        else
        {
            _sortedKeys.Add(key.ToArray());
        }
    }

    private class SortedView(SortedSet<byte[]>.Enumerator view, IKeyValueStore backingDb): ISortedView
    {
        public bool MoveNext()
        {
            return view.MoveNext();
        }

        public ReadOnlySpan<byte> CurrentKey => view.Current;
        public ReadOnlySpan<byte> CurrentValue => backingDb[CurrentKey];

        public void Dispose()
        {
        }
    }
}
