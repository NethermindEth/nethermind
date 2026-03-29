// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public class CacheCodeInfoRepository : ICodeInfoRepository
{
    private static readonly AssociativeCache<ValueHash256, CodeInfo> _codeCache = new(MemoryAllowance.CodeCacheSize);

    private readonly IWorldState _worldState;
    private readonly CodeInfoRepository _inner;

    public CacheCodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider)
    {
        _worldState = worldState;
        _inner = new CodeInfoRepository(worldState, precompileProvider, GetOrCacheCodeInfo);
    }

    private CodeInfo GetOrCacheCodeInfo(ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString)
        {
            return CodeInfo.Empty;
        }

        if (_codeCache.TryGet(in codeHash, out CodeInfo? cachedCodeInfo))
        {
            Metrics.IncrementCodeDbCache();
            return cachedCodeInfo;
        }

        cachedCodeInfo = CodeInfoRepository.GetCodeInfo(_worldState, in codeHash);
        _codeCache.Set(in codeHash, cachedCodeInfo);
        return cachedCodeInfo;
    }

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress) =>
        _inner.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);

    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec) =>
        _inner.GetExecutableCodeHash(address, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, out Address? delegatedAddress) =>
        _inner.TryGetDelegation(address, spec, out delegatedAddress);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        if (CodeInfoRepository.InsertCode(_worldState, code, codeOwner, spec, out ValueHash256 codeHash) && !_codeCache.Contains(in codeHash))
        {
            _codeCache.Set(in codeHash, CodeInfoFactory.CreateCodeInfo(code));
        }
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        bool result = CodeInfoRepository.SetDelegation(_worldState, codeSource, authority, spec, out ValueHash256 codeHash, out byte[] authorizedBuffer);
        if (result && codeSource != Address.Zero && !_codeCache.Contains(in codeHash))
        {
            _codeCache.Set(in codeHash, new CodeInfo(authorizedBuffer));
        }
    }

    internal static void Clear() => _codeCache.Clear();
}
