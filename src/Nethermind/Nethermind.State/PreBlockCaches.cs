// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly ISingleBlockProcessingCache<StorageCell, byte[]> _storageCache = new ConcurrentDictionaryCache<StorageCell, byte[]>(LockPartitions, InitialCapacity, _ => 0);
    private readonly ISingleBlockProcessingCache<AddressAsKey, Account> _stateCache = new ConcurrentDictionaryCache<AddressAsKey, Account>(LockPartitions, InitialCapacity, _ => 0);
    private readonly ConcurrentDictionary<NodeKey, byte[]?> _rlpCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<PrecompileCacheKey, (byte[], bool)> _precompileCache = new(LockPartitions, InitialCapacity);

    public Hash256? StateRoot { get; set; }

    public PreBlockCaches()
    {
        _clearCaches =
        [
            () => _storageCache.NoResizeClear() ? CacheType.Storage : CacheType.None,
            () => _stateCache.NoResizeClear() ? CacheType.State : CacheType.None,
            () => _rlpCache.NoResizeClear() ? CacheType.Rlp : CacheType.None,
            () => _precompileCache.NoResizeClear() ? CacheType.Precompile : CacheType.None
        ];
    }

    public ISingleBlockProcessingCache<StorageCell, byte[]> StorageCache => _storageCache;
    public ISingleBlockProcessingCache<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, (byte[], bool)> PrecompileCache => _precompileCache;

    public CacheType ClearCaches(Hash256? stateRoot, Hash256? postStateRoot = null)
    {
        CacheType isDirty = CacheType.None;
        for (int index = KeepStateCashes(stateRoot) ? 2 : 0; index < _clearCaches.Length; index++)
        {
            isDirty |= _clearCaches[index]();
        }

        StateRoot = postStateRoot ?? StateRoot;

        return isDirty;
    }

    private bool KeepStateCashes(Hash256? parentStateRoot) => parentStateRoot == StateRoot;

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode();
    }

    private class ConcurrentDictionaryCache<TKey, TValue> : ConcurrentDictionary<TKey, TValue>, ISingleBlockProcessingCache<TKey, TValue> where TKey : notnull
    {
        private readonly Func<TValue, long> _sizeCalculation;
        private readonly Func<TKey, TValue> _valueFactory;
        private readonly ThreadLocal<Func<TKey, TValue>> _localValueFactory = new();
        private long _size;

        public ConcurrentDictionaryCache(int lockPartitions, int initialCapacity, Func<TValue, long> sizeCalculation) : base(lockPartitions, initialCapacity)
        {
            _sizeCalculation = sizeCalculation;
            _valueFactory = ValueFactory;
        }

        TValue? ISingleBlockProcessingCache<TKey, TValue>.GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            // _localValueFactory.Value = valueFactory;
            return GetOrAdd(key, valueFactory);
        }

        private TValue ValueFactory(TKey key)
        {
            TValue value = _localValueFactory.Value(key);
            long size = _sizeCalculation(value);
            Interlocked.Add(ref _size, size);
            return value;
        }

        TValue ISingleBlockProcessingCache<TKey, TValue>.this[TKey key]
        {
            get => this[key];
            set
            {
                // long oldSize = TryGetValue(key, out TValue oldValue) ? _sizeCalculation(oldValue) : 0;
                this[key] = value;
                // Interlocked.Add(ref _size, _sizeCalculation(value) - oldSize);
            }
        }

        public bool NoResizeClear()
        {
            // _size = 0;
            return CollectionExtensions.NoResizeClear(this);
        }

        public long Size => _size;
    }
}

[Flags]
public enum CacheType
{
    None = 0,
    Storage = 0b1,
    State = 0b10,
    Rlp = 0b100,
    Precompile = 0b1000
}
