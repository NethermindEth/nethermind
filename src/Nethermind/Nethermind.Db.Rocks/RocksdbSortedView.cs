// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

internal class RocksdbSortedView(Iterator iterator, ReadOptions readOptions) : ISortedView
{
    private readonly Iterator _iterator = iterator;
    private readonly ReadOptions _readOptions = readOptions;
    private bool _started = false;

    // The read options own the iterate-bound buffers, so the iterator must go first.
    public void Dispose()
    {
        _iterator.Dispose();
        _readOptions.Dispose();
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
