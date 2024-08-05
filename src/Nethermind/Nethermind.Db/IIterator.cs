// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;
public interface IIterator<TKey, TValue> : IDisposable
{
    void Seek(TKey key);
    void Next();
    void Prev();
    bool Valid();
    TKey Key();
    TValue Value();
}
