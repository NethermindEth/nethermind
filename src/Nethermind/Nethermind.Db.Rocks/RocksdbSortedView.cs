// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

internal class RocksdbSortedView : ISortedView
{
    private readonly Iterator _iterator;
    private readonly IntPtr _lowerBound;
    private readonly IntPtr _upperBound;
    private bool _started = false;

    public RocksdbSortedView(Iterator iterator, ReadOptions? _ = null, IntPtr lowerBound = default, IntPtr upperBound = default)
    {
        _iterator = iterator;
        _lowerBound = lowerBound;
        _upperBound = upperBound;
    }

    public void Dispose()
    {
        _iterator.Dispose();
        if (_lowerBound != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_lowerBound);
        }
        if (_upperBound != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_upperBound);
        }
    }

    public bool StartBefore(ReadOnlySpan<byte> value)
    {
        if (_started)
            throw new InvalidOperationException($"{nameof(StartBefore)} can only be called before starting iteration.");

        _iterator.SeekForPrev(value);
        return _started = _iterator.Valid();
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
