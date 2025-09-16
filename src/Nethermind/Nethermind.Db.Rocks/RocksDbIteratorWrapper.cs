// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using RocksDbSharp;
using System;

namespace Nethermind.Db.Rocks;

public class RocksDbIteratorWrapper(Iterator iterator) : IIterator
{
    public void SeekToFirst() => iterator.SeekToFirst();
    public void Seek(ReadOnlySpan<byte> key) => iterator.Seek(key);
    public void SeekForPrev(ReadOnlySpan<byte> key) => iterator.SeekForPrev(key);
    public void Next() => iterator.Next();
    public void Prev() => iterator.Prev();
    public bool Valid() => iterator.Valid();
    public ReadOnlySpan<byte> Key() => iterator.GetKeySpan();
    public ReadOnlySpan<byte> Value() => iterator.GetValueSpan();
    public void Dispose() => iterator.Dispose();
}

