// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>The code most recently resolved, so a repeat skips the shared cache's probe.</summary>
    /// <remarks>
    /// A single reference is self-validating: <see cref="StaticCodeCache"/> assigns <c>CodeHash</c> when it
    /// stores, so matching against the hash re-read from the world state costs no allocation and cannot
    /// tear. Anything that changes an account's code — including a reverted deployment — produces a
    /// different hash and misses; there is nothing to invalidate. Under <c>NoopCodeCache</c> (witness
    /// generation, stateless execution) the hash stays default and the memo never fires, which is exactly
    /// the every-lookup-through-the-world-state behaviour that mode requires.
    /// </remarks>
    private CodeInfo? _lastResolved;

    private CodeInfo GetOrCacheCodeInfo(Address address, ValueHash256 codeHash, IReleaseSpec spec)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString)
        {
            return CodeInfo.Empty;
        }

        CodeInfo? lastResolved = _lastResolved;
        if (lastResolved is not null && lastResolved.CodeHash == codeHash)
        {
            // A memo hit is a cache hit: keep cache.code.hits and the cached-contracts-used stats counting.
            Metrics.IncrementCodeDbCache();
            return lastResolved;
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

        _lastResolved = cachedCodeInfo;
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
