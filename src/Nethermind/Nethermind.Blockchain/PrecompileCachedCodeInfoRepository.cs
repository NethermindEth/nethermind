// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain;

public class PrecompileCachedCodeInfoRepository : ICodeInfoRepository
{
    private readonly IWorldState _worldState;
    private readonly ICodeInfoRepository _baseCodeInfoRepository;
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _cachedPrecompile;
    /// <summary>The cached precompiles indexed by precompile number, as the base repository holds them.</summary>
    /// <remarks>This decorator answers precompile calls before the base repository sees them, so it needs
    /// an index of its own — without one the dictionary probe survives on the processing path, which is
    /// the one place it was worth removing. Built from <see cref="_cachedPrecompile"/> rather than from
    /// the provider, so an indexed hit returns the cache-wrapped <see cref="CodeInfo"/>.</remarks>
    private readonly CodeInfo?[] _cachedPrecompileArray;

    public PrecompileCachedCodeInfoRepository(
        IWorldState worldState,
        IPrecompileProvider precompileProvider,
        ICodeInfoRepository baseCodeInfoRepository,
        PrecompileCaches? precompileCaches)
    {
        _worldState = worldState;
        _baseCodeInfoRepository = baseCodeInfoRepository;
        _cachedPrecompile = precompileCaches is null
            ? precompileProvider.GetPrecompiles()
            : precompileProvider.GetPrecompiles().ToFrozenDictionary(kvp => kvp.Key, kvp => CreateCachedPrecompile(kvp, precompileCaches));
        _cachedPrecompileArray = CodeInfoRepository.BuildPrecompileArray(_cachedPrecompile);
    }

    public bool IsCodeOverridable => _baseCodeInfoRepository.IsCodeOverridable;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec,
        out Address? delegationAddress)
    {
        if (vmSpec.IsPrecompile(codeSource) && TryGetCachedPrecompile(codeSource, out CodeInfo? cachedCodeInfo))
        {
            // EIP-7928: mirror base CodeInfoRepository.GetCachedCodeInfo precompile path so the read lands in the BAL.
            _worldState.AddAccountRead(codeSource);
            delegationAddress = null;
            return cachedCodeInfo;
        }
        return _baseCodeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    /// <summary>Resolves a precompile's cached <see cref="CodeInfo"/> from its number, then from the map.</summary>
    /// <remarks>The map still has to answer for a number above the array — Taiko registers at 0x10001 — and
    /// for one the spec knows but this provider does not, which falls through to the base repository.</remarks>
    private bool TryGetCachedPrecompile(Address codeSource, [NotNullWhen(true)] out CodeInfo? cachedCodeInfo)
    {
        int index = codeSource.PrecompileIndexOrNegative();
        CodeInfo?[] byIndex = _cachedPrecompileArray;
        if ((uint)index < (uint)byIndex.Length && byIndex[index] is { } indexed)
        {
            cachedCodeInfo = indexed;
            return true;
        }

        return _cachedPrecompile.TryGetValue(codeSource, out cachedCodeInfo);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        _baseCodeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        _baseCodeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec,
        [NotNullWhen(true)] out Address? delegatedAddress) =>
        _baseCodeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);

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
