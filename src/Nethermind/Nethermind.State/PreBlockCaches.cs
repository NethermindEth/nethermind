// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<bool>[] _clearCaches;

    private readonly ConcurrentDictionary<StorageCell, byte[]> _storageCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<AddressAsKey, Account> _stateCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<NodeKey, byte[]?> _rlpCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> _precompileCache = new(LockPartitions, InitialCapacity);

    public PreBlockCaches()
    {
        _clearCaches =
        [
            _storageCache.NoResizeClear,
            _stateCache.NoResizeClear,
            _rlpCache.NoResizeClear,
            _precompileCache.NoResizeClear
        ];
    }

    public ConcurrentDictionary<StorageCell, byte[]> StorageCache => _storageCache;
    public ConcurrentDictionary<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> PrecompileCache => _precompileCache;

    public bool ClearImmediate()
    {
        bool isDirty = false;
        foreach (Func<bool> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        return isDirty;
    }

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode()
        {
            uint crc = (uint)Address.GetHashCode();
            ReadOnlySpan<byte> span = Data.Span;
            var longSize = span.Length / sizeof(ulong) * sizeof(ulong);
            if (longSize > 0)
            {
                foreach (ulong ul in MemoryMarshal.Cast<byte, ulong>(span[..longSize]))
                {
                    crc = BitOperations.Crc32C(crc, ul);
                }
                foreach (byte b in span[longSize..])
                {
                    crc = BitOperations.Crc32C(crc, b);
                }
            }
            else
            {
                foreach (byte b in span)
                {
                    crc = BitOperations.Crc32C(crc, b);
                }
            }

            return (int)crc;
        }
    }
}
