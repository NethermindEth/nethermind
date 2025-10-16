// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Caching;
public interface IClockCache<TKey, TValue> where TKey : struct, IEquatable<TKey>
{
    TValue Get(TKey key);
    bool Set(TKey key, TValue val);
    bool TryGet(TKey key, out TValue value);
}
