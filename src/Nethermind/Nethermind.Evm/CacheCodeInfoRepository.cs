// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public class CacheCodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider) : CodeInfoRepository(worldState, precompileProvider)
{
    private static readonly CodeLruCache _codeCache = new();

    protected override CodeInfo InternalGetCodeInfo(in ValueHash256 codeHash, IReleaseSpec vmSpec)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString)
        {
            return CodeInfo.Empty;
        }

        CodeInfo? cachedCodeInfo = _codeCache.Get(in codeHash);
        if (cachedCodeInfo is null)
        {
            cachedCodeInfo = GetCodeInfo(_worldState, codeHash, vmSpec);
            _codeCache.Set(in codeHash, cachedCodeInfo);
        }
        else
        {
            Metrics.IncrementCodeDbCache();
        }

        return cachedCodeInfo;
    }

    public override void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        if (InsertCode(_worldState, code, codeOwner, spec, out ValueHash256 codeHash) && _codeCache.Get(in codeHash) is null)
        {
            _codeCache.Set(in codeHash, CodeInfoFactory.CreateCodeInfo(code, spec));
        }
    }

    public override void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        bool result = SetDelegation(_worldState, codeSource, authority, spec, out ValueHash256 codeHash, out byte[] authorizedBuffer);
        if (result && codeSource != Address.Zero && _codeCache.Get(in codeHash) is null)
        {
            _codeCache.Set(codeHash, new CodeInfo(authorizedBuffer));
        }
    }

    private sealed class CodeLruCache
    {
        private const int CacheCount = 16;
        private const int CacheMax = CacheCount - 1;
        private readonly ClockCache<ValueHash256, CodeInfo>[] _caches;

        public CodeLruCache()
        {
            _caches = new ClockCache<ValueHash256, CodeInfo>[CacheCount];
            for (int i = 0; i < _caches.Length; i++)
            {
                // Cache per nibble to reduce contention as TxPool is very parallel
                _caches[i] = new ClockCache<ValueHash256, CodeInfo>(MemoryAllowance.CodeCacheSize / CacheCount);
            }
        }

        public CodeInfo? Get(in ValueHash256 codeHash)
        {
            ClockCache<ValueHash256, CodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Get(codeHash);
        }

        public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
        {
            ClockCache<ValueHash256, CodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            cache.Set(codeHash, codeInfo);
        }

        private static int GetCacheIndex(in ValueHash256 codeHash) => codeHash.Bytes[^1] & CacheMax;
    }
}

