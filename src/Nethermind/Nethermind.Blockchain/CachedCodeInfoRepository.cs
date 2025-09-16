// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Blockchain;

public class CachedCodeInfoRepository(
    IPrecompileProvider precompileProvider,
    ICodeInfoRepository baseCodeInfoRepository,
    ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)>? precompileCache) : ICodeInfoRepository
{
    private readonly FrozenDictionary<AddressAsKey, PrecompileInfo> _cachedPrecompile = precompileCache is null
        ? precompileProvider.GetPrecompiles()
        : precompileProvider.GetPrecompiles().ToFrozenDictionary(kvp => kvp.Key, kvp => CreateCachedPrecompile(kvp, precompileCache));

    public ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec,
        out Address? delegationAddress)
    {
        if (IsPrecompile(codeSource, vmSpec) && _cachedPrecompile.TryGetValue(codeSource, out var cachedCodeInfo))
        {
            delegationAddress = null;
            return cachedCodeInfo;
        }
        return baseCodeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec)
    {
        return baseCodeInfoRepository.GetExecutableCodeHash(address, spec);
    }

    public bool IsPrecompile(Address address, IReleaseSpec spec)
    {
        return baseCodeInfoRepository.IsPrecompile(address, spec);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        baseCodeInfoRepository.InsertCode(code, codeOwner, spec);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        baseCodeInfoRepository.SetDelegation(codeSource, authority, spec);
    }

    public bool TryGetDelegation(Address address, IReleaseSpec spec,
        [NotNullWhen(true)] out Address? delegatedAddress)
    {
        return baseCodeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);
    }

    private static PrecompileInfo CreateCachedPrecompile(
        in KeyValuePair<AddressAsKey, PrecompileInfo> originalPrecompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)> cache) =>
        new PrecompileInfo(new CachedPrecompile(originalPrecompile.Key.Value, originalPrecompile.Value.Precompile!, cache));

    private class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)> cache) : IPrecompile
    {
        public static Address Address => Address.Zero;

        public static string Name => "";

        public long BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            PreBlockCaches.PrecompileCacheKey key = new(address, inputData);
            if (!cache.TryGetValue(key, out (byte[], bool) result))
            {
                result = precompile.Run(inputData, releaseSpec);
                // we need to rebuild the key with data copy as the data can be changed by VM processing
                key = new PreBlockCaches.PrecompileCacheKey(address, inputData.ToArray());
                cache.TryAdd(key, result);
            }

            return result;
        }
    }
}
