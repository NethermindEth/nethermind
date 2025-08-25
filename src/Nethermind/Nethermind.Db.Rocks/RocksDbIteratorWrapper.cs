// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using RocksDbSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Db.Rocks;

public class RocksDbIteratorWrapper : IIterator<byte[], byte[]>
{
    private readonly Iterator _iterator;

    public RocksDbIteratorWrapper(Iterator iterator)
    {
        _iterator = iterator;
    }

    public void SeekToFirst()
    {
        _iterator.SeekToFirst();
    }

    public void Seek(byte[] key)
    {
        _iterator.Seek(key);
    }

    public void SeekForPrev(byte[] key)
    {
        _iterator.SeekForPrev(key);
    }

    public void SeekForPrev(ReadOnlySpan<byte> key)
    {
        _iterator.SeekForPrev(key);
    }

    public void Next()
    {
        _iterator.Next();
    }

    public void Prev()
    {
        _iterator.Prev();
    }

    public bool Valid()
    {
        return _iterator.Valid();
    }

    public byte[] Key()
    {
        return _iterator.Key();
    }

    public byte[] Value()
    {
        return _iterator.Value();
    }

    public void Dispose()
    {
        _iterator.Dispose();
    }
}

