// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public interface IIterator : IDisposable
{
    void SeekToFirst();
    void Seek(ReadOnlySpan<byte> key);
    void SeekForPrev(ReadOnlySpan<byte> key);
    void Next();
    void Prev();
    bool Valid();
    ReadOnlySpan<byte> Key();
    ReadOnlySpan<byte> Value();
}
