// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly ILogger _logger;
    private readonly Action _clearStorageCache;
    private readonly Action _clearStateCache;
    private readonly Action _clearRlpCache;
    private readonly Action _clearPrecompileCache;

    private ConcurrentDictionary<StorageCell, byte[]> _storageCache = new(LockPartitions, InitialCapacity);
    private ConcurrentDictionary<AddressAsKey, Account> _stateCache = new(LockPartitions, InitialCapacity);
    private ConcurrentDictionary<NodeKey, byte[]?> _rlpCache = new(LockPartitions, InitialCapacity);
    private ConcurrentDictionary<PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> _precompileCache = new(LockPartitions, InitialCapacity);

    public PreBlockCaches(ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _clearStorageCache = () => _storageCache.NoResizeClear();
        _clearStateCache = () => _stateCache.NoResizeClear();
        _clearRlpCache = () => _rlpCache.NoResizeClear();
        _clearPrecompileCache = () => _precompileCache.NoResizeClear();
    }

    public ConcurrentDictionary<StorageCell, byte[]> StorageCache => _storageCache;
    public ConcurrentDictionary<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache { get; } = new(LockPartitions, InitialCapacity);
    public ConcurrentDictionary<PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> PrecompileCache { get; } = new(LockPartitions, InitialCapacity);

    public async Task ClearCachesInBackground()
    {
        Task t0 = Task.CompletedTask;
        Task t1 = Task.CompletedTask;
        Task t2 = Task.CompletedTask;
        Task t3 = Task.CompletedTask;

        bool isDirty = false;
        if (!_storageCache.IsEmpty)
        {
            isDirty = true;
            t0 = Task.Run(_clearStorageCache);
        }
        if (!_stateCache.IsEmpty)
        {
            isDirty = true;
            t1 = Task.Run(_clearStateCache);
        }
        if (!_rlpCache.IsEmpty)
        {
            isDirty = true;
            t2 = Task.Run(_clearRlpCache);
        }
        if (!_precompileCache.IsEmpty)
        {
            isDirty = true;
            t3 = Task.Run(_clearPrecompileCache);
        }

        if (isDirty)
        {
            await Task.WhenAll(t0, t1, t2, t3);
        }
    }

    public bool ClearImmediate()
    {
        bool isDirty = false;
        if (!_storageCache.IsEmpty)
        {
            isDirty = true;
            _storageCache.NoResizeClear();
        }
        if (!_stateCache.IsEmpty)
        {
            isDirty = true;
            _stateCache.NoResizeClear();
        }
        if (!_rlpCache.IsEmpty)
        {
            isDirty = true;
            _rlpCache.NoResizeClear();
        }
        if (!_precompileCache.IsEmpty)
        {
            isDirty = true;
            _precompileCache.NoResizeClear();
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
