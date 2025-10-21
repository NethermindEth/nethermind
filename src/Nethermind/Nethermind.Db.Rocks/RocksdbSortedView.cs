// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

internal class RocksdbSortedView : ISortedView
{
    private readonly Iterator _iterator;
    private bool _started = false;

    public RocksdbSortedView(Iterator iterator)
    {
        _iterator = iterator;
    }

    public void Dispose()
    {
        _iterator.Dispose();
    }

    public bool MoveNext()
    {
        if (!_started)
        {
            _iterator.SeekToFirst();
            _started = true;
        }
        else
        {
            _iterator.Next();
        }
        return _iterator.Valid();
    }

    public ReadOnlySpan<byte> CurrentKey => _iterator.GetKeySpan();
    public ReadOnlySpan<byte> CurrentValue => _iterator.GetValueSpan();
}
