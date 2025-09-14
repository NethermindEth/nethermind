// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

// TODO: remove generics?
public interface IIterator<TKey, out TValue> : IDisposable
{
    void SeekToFirst();
    void Seek(ReadOnlySpan<byte> key);
    void SeekForPrev(TKey key);
    void SeekForPrev(ReadOnlySpan<byte> key);
    void Next();
    void Prev();
    bool Valid();
    TKey Key();
    TValue Value();
}
