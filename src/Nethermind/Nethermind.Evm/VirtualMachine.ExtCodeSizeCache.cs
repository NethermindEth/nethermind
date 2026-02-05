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
    private const int MaxExtCodeSizeCacheEntries = 256;
    private Dictionary<AddressAsKey, ExtCodeSizeCacheEntry>? _extCodeSizeCache;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint GetExtCodeSizeCached(Address address, IReleaseSpec spec)
    {
        _extCodeSizeCache ??= new Dictionary<AddressAsKey, ExtCodeSizeCacheEntry>(8);
        AddressAsKey key = address;

        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (_extCodeSizeCache.TryGetValue(key, out ExtCodeSizeCacheEntry entry) && entry.CodeHash == codeHash)
        {
            return entry.CodeSize;
        }

        ICodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _);
        uint codeSize = codeInfo is EofCodeInfo ? (uint)EofValidator.MAGIC.Length : (uint)codeInfo.CodeSpan.Length;

        StoreCacheEntry(key, codeHash, codeSize, codeInfo);
        return codeSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetExtCodeSizeCache()
    {
        // Cache is keyed by code hash to remain correct when code changes mid-transaction.
        _extCodeSizeCache?.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ICodeInfo GetExtCodeInfoCached(Address address, IReleaseSpec spec)
    {
        _extCodeSizeCache ??= new Dictionary<AddressAsKey, ExtCodeSizeCacheEntry>(8);
        AddressAsKey key = address;

        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (_extCodeSizeCache.TryGetValue(key, out ExtCodeSizeCacheEntry entry)
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
        if (_extCodeSizeCache!.Count >= MaxExtCodeSizeCacheEntries)
        {
            _extCodeSizeCache.Clear();
        }
        _extCodeSizeCache[key] = new ExtCodeSizeCacheEntry(codeHash, codeSize, codeInfo);
    }

    private readonly record struct ExtCodeSizeCacheEntry(ValueHash256 CodeHash, uint CodeSize, ICodeInfo? CodeInfo);
}
