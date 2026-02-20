// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    private const int DefaultMaxExtCodeCacheEntries = 1024;
    private static int _maxExtCodeCacheEntries = DefaultMaxExtCodeCacheEntries;
    private Dictionary<AddressAsKey, ExtCodeCacheEntry>? _extCodeCache;
    private long _extCodeCacheBlockNumber = long.MinValue;

    /// <summary>
    /// Gets the maximum number of entries kept in the EXTCODE* cache.
    /// </summary>
    public static int MaxExtCodeCacheEntries => _maxExtCodeCacheEntries;

    /// <summary>
    /// Sets the maximum number of entries kept in the EXTCODE* cache.
    /// Use <c>0</c> to disable the cache.
    /// </summary>
    /// <param name="maxEntries">The maximum number of cache entries. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxEntries"/> is negative.</exception>
    public static void SetMaxExtCodeCacheEntries(int maxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);
        _maxExtCodeCacheEntries = maxEntries;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint GetExtCodeSizeCached(Address address, IReleaseSpec spec)
        => ResolveExtCodeCacheEntry(address, spec).CodeSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetExtCodeCache()
    {
        // Cache is keyed by address, with code hash validation to remain correct when code changes mid-transaction.
        _extCodeCache?.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetExtCodeCacheForBlock()
    {
        long blockNumber = BlockExecutionContext.Header.Number;
        if (_extCodeCacheBlockNumber != blockNumber)
        {
            _extCodeCacheBlockNumber = blockNumber;
            ResetExtCodeCache();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CodeInfo GetExtCodeInfoCached(Address address, IReleaseSpec spec)
        => ResolveExtCodeCacheEntry(address, spec).CodeInfo;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ExtCodeCacheEntry ResolveExtCodeCacheEntry(Address address, IReleaseSpec spec)
    {
        if (_maxExtCodeCacheEntries == 0)
        {
            CodeInfo uncachedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _);
            return CreateExtCodeCacheEntry(default, uncachedCodeInfo);
        }

        _extCodeCache ??= new Dictionary<AddressAsKey, ExtCodeCacheEntry>(8);
        AddressAsKey key = address;

        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (_extCodeCache.TryGetValue(key, out ExtCodeCacheEntry entry) && entry.CodeHash == codeHash)
        {
            return entry;
        }

        CodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(address, in codeHash, spec);
        ExtCodeCacheEntry refreshedEntry = CreateExtCodeCacheEntry(in codeHash, codeInfo);
        StoreCacheEntry(key, in refreshedEntry);
        return refreshedEntry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ExtCodeCacheEntry CreateExtCodeCacheEntry(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        uint codeSize = codeInfo is EofCodeInfo ? (uint)EofValidator.MAGIC.Length : (uint)codeInfo.CodeSpan.Length;
        return new ExtCodeCacheEntry(codeHash, codeSize, codeInfo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreCacheEntry(AddressAsKey key, in ExtCodeCacheEntry entry)
    {
        if (_maxExtCodeCacheEntries == 0)
        {
            return;
        }

        Dictionary<AddressAsKey, ExtCodeCacheEntry> extCodeCache = _extCodeCache!;

        // Under high-cardinality workloads, clearing the whole dictionary causes avoidable churn.
        // Once capacity is reached, keep current hot set and skip admitting new keys.
        // Still allow updating existing keys so hot entries can be refreshed.
        if (extCodeCache.Count >= _maxExtCodeCacheEntries && !extCodeCache.ContainsKey(key))
        {
            return;
        }

        extCodeCache[key] = entry;
    }

    private readonly record struct ExtCodeCacheEntry(ValueHash256 CodeHash, uint CodeSize, CodeInfo CodeInfo);
}
