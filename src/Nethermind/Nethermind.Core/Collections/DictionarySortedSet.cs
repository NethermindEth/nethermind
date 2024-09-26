// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Nethermind.Core.Collections;

public class DictionarySortedSet<TKey, TValue> : EnhancedSortedSet<KeyValuePair<TKey, TValue>>, IDictionary<TKey, TValue>
{
    public DictionarySortedSet() : base(GetComparer()) { }

    public DictionarySortedSet(IComparer<TKey> comparer) : base(GetComparer(comparer)) { }


    public DictionarySortedSet(IEnumerable<KeyValuePair<TKey, TValue>> collection) : base(collection, GetComparer()) { }

    public DictionarySortedSet(IEnumerable<KeyValuePair<TKey, TValue>> collection, IComparer<TKey> comparer) : base(collection, GetComparer(comparer)) { }

    private static IComparer<KeyValuePair<TKey, TValue>> GetComparer(IComparer<TKey>? comparer = null) =>
        new KeyValuePairKeyOnlyComparer(comparer ?? Comparer<TKey>.Default);

    public bool Add(TKey key, TValue value) => Add(new KeyValuePair<TKey, TValue>(key, value));

#pragma warning disable 8604
    // fixed C# 9
    public bool Remove(TKey key) => Remove(new KeyValuePair<TKey, TValue>(key, default));
#pragma warning restore 8604

    public bool TryGetValue(TKey key, out TValue value)
    {
#pragma warning disable 8604
        // fixed C# 9
        if (TryGetValue(new KeyValuePair<TKey, TValue>(key, default), out var found))
#pragma warning restore 8604
        {
            value = found.Value;
            return true;
        }

#pragma warning disable 8601
        // fixed C# 9
        value = default;
#pragma warning restore 8601
        return false;
    }

    public TValue this[TKey key]
    {
        get
        {
            if (!TryGetValue(key, out TValue? value))
            {
                ThrowKeyNotFoundException(key);
            }
            return value;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(key);

            Add(key, value);
        }
    }

    public ICollection<TKey> Keys => this.Select(kv => kv.Key).ToArray();
    public ICollection<TValue> Values => this.Select(kv => kv.Value).ToArray();

#pragma warning disable 8604
    // fixed C# 9
    void IDictionary<TKey, TValue>.Add(TKey key, TValue value) => Add(key, value);
    public bool ContainsKey(TKey key) => Contains(new KeyValuePair<TKey, TValue>(key, default));
#pragma warning restore 8604

    /// <summary>Throws a KeyNotFoundException.</summary>
    /// <remarks>Separate from ThrowHelper to avoid boxing at call site while reusing this generic instantiation.</remarks>
    [DoesNotReturn]
    private static void ThrowKeyNotFoundException(TKey key) =>
        throw new KeyNotFoundException($"Key not found {key}");

    private sealed class KeyValuePairKeyOnlyComparer : Comparer<KeyValuePair<TKey, TValue>>
    {
        private readonly IComparer<TKey> _keyComparer;

        public KeyValuePairKeyOnlyComparer(IComparer<TKey>? keyComparer = null)
        {
            _keyComparer = keyComparer ?? Comparer<TKey>.Default;
        }

        public override int Compare(KeyValuePair<TKey, TValue> x, KeyValuePair<TKey, TValue> y)
            => _keyComparer.Compare(x.Key, y.Key);
    }

}
