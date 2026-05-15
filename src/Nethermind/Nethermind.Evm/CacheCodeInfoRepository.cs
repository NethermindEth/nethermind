// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public class CacheCodeInfoRepository : ICodeInfoRepository
{
    private static readonly CodeLruCache _codeCache = new();

    private readonly IWorldState _worldState;
    private readonly CodeInfoRepository _inner;

    public CacheCodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider)
    {
        _worldState = worldState;
        _inner = new CodeInfoRepository(worldState, precompileProvider, GetOrCacheCodeInfo);
    }

    private CodeInfo GetOrCacheCodeInfo(Address address, ValueHash256 codeHash, IReleaseSpec spec)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString)
        {
            return CodeInfo.Empty;
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

        return cachedCodeInfo;
    }

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress) =>
        _inner.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, out Address? delegatedAddress) =>
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

    internal static void Clear() => _codeCache.Clear();

    private sealed class CodeLruCache
    {
        private readonly AssociativeCache<ValueHash256, CodeInfo> _cache = new(MemoryAllowance.CodeCacheSize);

        public CodeInfo? Get(in ValueHash256 codeHash) => _cache.Get(in codeHash);

        public void Set(in ValueHash256 codeHash, CodeInfo codeInfo) => _cache.Set(in codeHash, codeInfo);

        public bool TryGet(in ValueHash256 codeHash, [NotNullWhen(true)] out CodeInfo? codeInfo)
        {
            codeInfo = Get(in codeHash);
            return codeInfo is not null;
        }

        internal void Clear() => _cache.Clear();
    }
}
