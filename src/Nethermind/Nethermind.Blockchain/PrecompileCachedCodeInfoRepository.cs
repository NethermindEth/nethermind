// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain;

public class PrecompileCachedCodeInfoRepository(
    IWorldState worldState,
    IPrecompileProvider precompileProvider,
    ICodeInfoRepository baseCodeInfoRepository,
    ClockCache<PreBlockCaches.PrecompileCacheKey, Result<byte[]>>? precompileCache) : ICodeInfoRepository
{
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _cachedPrecompile = precompileCache is null
        ? precompileProvider.GetPrecompiles()
        : precompileProvider.GetPrecompiles().ToFrozenDictionary(kvp => kvp.Key, kvp => CreateCachedPrecompile(kvp, precompileCache));

    public bool IsCodeOverridable => baseCodeInfoRepository.IsCodeOverridable;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec,
        out Address? delegationAddress)
    {
        if (vmSpec.IsPrecompile(codeSource) && _cachedPrecompile.TryGetValue(codeSource, out CodeInfo cachedCodeInfo))
        {
            // EIP-7928: mirror base CodeInfoRepository.GetCachedCodeInfo precompile path so the read lands in the BAL.
            worldState.AddAccountRead(codeSource);
            delegationAddress = null;
            return cachedCodeInfo;
        }
        return baseCodeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        baseCodeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        baseCodeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec,
        [NotNullWhen(true)] out Address? delegatedAddress) =>
        baseCodeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);

    private static CodeInfo CreateCachedPrecompile(
        in KeyValuePair<AddressAsKey, CodeInfo> originalPrecompile,
        ClockCache<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache)
    {
        IPrecompile precompile = originalPrecompile.Value.Precompile!;

        return !precompile.SupportsCaching
            ? originalPrecompile.Value
            : new CodeInfo(new CachedPrecompile(originalPrecompile.Key.Value, precompile, cache));
    }

    private class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        ClockCache<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache) : IPrecompile
    {
        // Bounds retained bytes, not just entries: gas admits ~100KB hash inputs, which at full
        // capacity would otherwise let the now-persistent cache grow to GB-class.
        private const int MaxCachedEntryBytes = 2048;

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            ReadOnlyMemory<byte> effectiveInput = precompile.NormalizeInput(inputData);
            PreBlockCaches.PrecompileCacheKey key = new(address, effectiveInput, releaseSpec);
            if (!cache.TryGet(key, out Result<byte[]> result))
            {
                result = precompile.Run(inputData, releaseSpec);

                // no need to spend memory on caching invalid-length inputs
                // it's fast to check and is the first verification done by a precompile
                if (result is { IsError: true, Error: Errors.InvalidInputLength })
                    return result;

                if (effectiveInput.Length + (result.Data?.Length ?? 0) > MaxCachedEntryBytes)
                    return result;

                // we need to rebuild the key with data copy as the data can be changed by VM processing
                // effective-input bounds are expected to remain the same
                key = new(address, effectiveInput.ToArray(), releaseSpec);
                cache.Set(key, result);
            }

            return result;
        }
    }
}
