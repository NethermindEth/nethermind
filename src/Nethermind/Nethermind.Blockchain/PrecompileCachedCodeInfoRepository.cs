// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
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
    ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>>? precompileCache) : ICodeInfoRepository
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
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache)
    {
        IPrecompile precompile = originalPrecompile.Value.Precompile!;

        return !precompile.SupportsCaching
            ? originalPrecompile.Value
            : new CodeInfo(new CachedPrecompile(originalPrecompile.Key.Value, precompile, cache));
    }

    private class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache) : IPrecompile
    {
        [ThreadStatic] private static CachedPrecompile? t_lastPrecompile;
        [ThreadStatic] private static byte[]? t_lastInput;
        [ThreadStatic] private static Result<byte[]> t_lastResult;

        public long BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            ReadOnlyMemory<byte> effectiveInput = precompile.NormalizeInput(inputData);
            if (TryGetThreadCachedResult(effectiveInput.Span, out Result<byte[]> result))
            {
                return result;
            }

            PreBlockCaches.PrecompileCacheKey key = new(address, effectiveInput);
            if (!cache.TryGetValue(key, out result))
            {
                result = precompile.Run(inputData, releaseSpec);

                // no need to spend memory on caching invalid-length inputs
                // it's fast to check and is the first verification done by a precompile
                if (result is { IsError: true, Error: Errors.InvalidInputLength })
                    return result;

                // we need to rebuild the key with data copy as the data can be changed by VM processing
                // effective-input bounds are expected to remain the same
                byte[] copiedInput = effectiveInput.ToArray();
                key = new(address, copiedInput);
                cache.TryAdd(key, result);
                CacheThreadResult(copiedInput, result);
                return result;
            }

            CacheThreadResult(effectiveInput.Span, result);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetThreadCachedResult(ReadOnlySpan<byte> effectiveInput, out Result<byte[]> result)
        {
            byte[]? lastInput = t_lastInput;
            if (ReferenceEquals(t_lastPrecompile, this)
                && lastInput is not null
                && effectiveInput.SequenceEqual(lastInput))
            {
                result = t_lastResult;
                return true;
            }

            result = default;
            return false;
        }

        private void CacheThreadResult(ReadOnlySpan<byte> effectiveInput, Result<byte[]> result)
        {
            byte[]? lastInput = t_lastInput;
            if (lastInput is null || lastInput.Length != effectiveInput.Length)
            {
                lastInput = effectiveInput.ToArray();
                t_lastInput = lastInput;
            }
            else
            {
                effectiveInput.CopyTo(lastInput);
            }

            t_lastPrecompile = this;
            t_lastResult = result;
        }
    }
}
