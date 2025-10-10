// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State;

public interface ISingleBlockProcessingCache<TKey, TValue>
{
    TValue? GetOrAdd(TKey key, Func<TKey, TValue> valueFactory);
    bool TryGetValue(TKey key, out TValue value);
    TValue this[TKey key] { get; set; }
    bool NoResizeClear();
}
