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

    /// <summary>
    /// Gets the maximum number of entries kept in the per-transaction EXTCODE* cache.
    /// </summary>
    public static int MaxExtCodeCacheEntries => _maxExtCodeCacheEntries;

    /// <summary>
    /// Sets the maximum number of entries kept in the per-transaction EXTCODE* cache.
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
    {
        if (_maxExtCodeCacheEntries == 0)
        {
            ICodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _);
            return codeInfo is EofCodeInfo ? (uint)EofValidator.MAGIC.Length : (uint)codeInfo.CodeSpan.Length;
        }

        _extCodeCache ??= new Dictionary<AddressAsKey, ExtCodeCacheEntry>(8);
        AddressAsKey key = address;

        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (_extCodeCache.TryGetValue(key, out ExtCodeCacheEntry entry) && entry.CodeHash == codeHash)
        {
            return entry.CodeSize;
        }

        ICodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _);
        uint codeSize = codeInfo is EofCodeInfo ? (uint)EofValidator.MAGIC.Length : (uint)codeInfo.CodeSpan.Length;

        StoreCacheEntry(key, codeHash, codeSize, codeInfo);
        return codeSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetExtCodeCache()
    {
        // Cache is keyed by code hash to remain correct when code changes mid-transaction.
        _extCodeCache?.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ICodeInfo GetExtCodeInfoCached(Address address, IReleaseSpec spec)
    {
        if (_maxExtCodeCacheEntries == 0)
        {
            return _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _);
        }

        _extCodeCache ??= new Dictionary<AddressAsKey, ExtCodeCacheEntry>(8);
        AddressAsKey key = address;

        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (_extCodeCache.TryGetValue(key, out ExtCodeCacheEntry entry)
            && entry.CodeHash == codeHash
            && entry.CodeInfo is not null)
        {
            return entry.CodeInfo;
        }

        ICodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _);
        uint codeSize = codeInfo is EofCodeInfo ? (uint)EofValidator.MAGIC.Length : (uint)codeInfo.CodeSpan.Length;
        StoreCacheEntry(key, codeHash, codeSize, codeInfo);
        return codeInfo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreCacheEntry(AddressAsKey key, in ValueHash256 codeHash, uint codeSize, ICodeInfo? codeInfo)
    {
        if (_maxExtCodeCacheEntries == 0)
        {
            return;
        }

        if (_extCodeCache!.Count >= _maxExtCodeCacheEntries)
        {
            _extCodeCache.Clear();
        }
        _extCodeCache[key] = new ExtCodeCacheEntry(codeHash, codeSize, codeInfo);
    }

    private readonly record struct ExtCodeCacheEntry(ValueHash256 CodeHash, uint CodeSize, ICodeInfo? CodeInfo);
}
