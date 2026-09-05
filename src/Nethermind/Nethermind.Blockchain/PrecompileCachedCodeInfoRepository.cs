// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Precompiles;
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
    PrecompileCaches? precompileCaches) : ICodeInfoRepository
{
    private readonly PrecompileTable<CodeInfo> _cachedPrecompile = new(precompileCaches is null
        ? precompileProvider.GetPrecompiles()
        : precompileProvider.GetPrecompiles().ToFrozenDictionary(kvp => kvp.Key, kvp => CreateCachedPrecompile(kvp, precompileCaches)));

    public bool IsCodeOverridable => baseCodeInfoRepository.IsCodeOverridable;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec,
        out Address? delegationAddress)
    {
        if (vmSpec.IsPrecompile(codeSource) && _cachedPrecompile.TryGetValue(codeSource, out CodeInfo cachedCodeInfo))
        {
            // EIP-7928: mirror base CodeInfoRepository.GetCachedCodeInfo precompile path so the read lands in the BAL.
            worldState.AddAccountRead(codeSource);
            // TESTING: call-frequency instrumentation, testing branch only - never merge to master.
            Core.Precompiles.PrecompileLookupCounters.CachedCodeInfoLookups.Increment();
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
        PrecompileCaches caches)
    {
        IPrecompile precompile = originalPrecompile.Value.Precompile!;

        return !precompile.SupportsCaching || !caches.TryGetPartition(originalPrecompile.Key.Value, out PrecompileCaches.Partition? partition)
            ? originalPrecompile.Value
            : new CodeInfo(new CachedPrecompile(originalPrecompile.Key.Value, precompile, partition));
    }

    private class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        PrecompileCaches.Partition cache) : IPrecompile
    {
        public string Name => precompile.Name;

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            ReadOnlyMemory<byte> effectiveInput = precompile.NormalizeInput(inputData);
            PrecompileCaches.Key key = new(address, effectiveInput, releaseSpec);
            if (cache.TryGet(key, out Result<byte[]> result))
            {
                return result;
            }

            result = precompile.Run(inputData, releaseSpec);

            // no need to spend memory on caching invalid-length inputs
            // it's fast to check and is the first verification done by a precompile
            if (result is { IsError: true, Error: Errors.InvalidInputLength })
                return result;

            cache.TryAdd(key, result);
            return result;
        }
    }
}
