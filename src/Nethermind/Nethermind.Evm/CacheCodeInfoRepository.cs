// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public class CacheCodeInfoRepository : ICodeInfoRepository
{
    private readonly IWorldState _worldState;
    private readonly ICodeCache _codeCache;
    private readonly CodeInfoRepository _inner;

    public CacheCodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider, ICodeCache codeCache)
    {
        _worldState = worldState;
        _codeCache = codeCache;
        _inner = new CodeInfoRepository(worldState, precompileProvider, GetOrCacheCodeInfo);
    }

    private const int MemoCapacity = 4;
    private const int MemoHitsPerTickerRefresh = 64;

    private struct MemoEntry
    {
        public CodeInfo? CodeInfo;
        public int Hits;
    }

    [InlineArray(MemoCapacity)]
    private struct MemoEntries
    {
        private MemoEntry _first;
    }

    private MemoEntries _resolvedCodeInfos;
    private int _nextMemoIndex;

    private CodeInfo GetOrCacheCodeInfo(Address address, ValueHash256 codeHash, IReleaseSpec spec)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString)
        {
            return CodeInfo.Empty;
        }

        int memoIndex = -1;
        for (int i = 0; i < MemoCapacity; i++)
        {
            ref MemoEntry entry = ref _resolvedCodeInfos[i];
            CodeInfo? codeInfo = entry.CodeInfo;
            if (codeInfo is not null && codeInfo.CodeHash == codeHash)
            {
                if (++entry.Hits < MemoHitsPerTickerRefresh)
                {
                    Metrics.IncrementCodeDbCache();
                    return codeInfo;
                }

                entry.Hits = 0;
                memoIndex = i;
                break;
            }
        }

        if (memoIndex < 0)
        {
            memoIndex = _nextMemoIndex;
            _nextMemoIndex = (_nextMemoIndex + 1) % MemoCapacity;
        }

        CodeInfo? cachedCodeInfo = _codeCache.Get(in codeHash);
        if (cachedCodeInfo is null)
        {
            cachedCodeInfo = CodeInfoRepository.GetCodeInfo(_worldState, address, in codeHash);
            _codeCache.Set(in codeHash, cachedCodeInfo);
        }
        else
        {
            Metrics.IncrementCodeDbCache();
        }

        ref MemoEntry resolved = ref _resolvedCodeInfos[memoIndex];
        resolved.Hits = 0;
        resolved.CodeInfo = cachedCodeInfo;
        return cachedCodeInfo;
    }

    public bool IsCodeOverridable => _inner.IsCodeOverridable;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress) =>
        _inner.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        _inner.TryGetDelegation(address, spec, out delegatedAddress);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        if (CodeInfoRepository.InsertCode(_worldState, code, codeOwner, spec, out ValueHash256 codeHash) && _codeCache.Get(in codeHash) is null)
        {
            _codeCache.Set(in codeHash, CodeInfoFactory.CreateCodeInfo(code));
        }
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        bool result = CodeInfoRepository.SetDelegation(_worldState, codeSource, authority, spec, out ValueHash256 codeHash, out byte[] authorizedBuffer);
        if (result && codeSource != Address.Zero && _codeCache.Get(in codeHash) is null)
        {
            _codeCache.Set(in codeHash, new CodeInfo(authorizedBuffer));
        }
    }
}
