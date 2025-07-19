// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.State;

namespace Nethermind.Blockchain;

public static class CachedCodeInfoRepository
{
    public static CodeInfoRepository CreateCodeInfoRepository(
        FrozenDictionary<AddressAsKey, PrecompileInfo> precompiles,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)>? precompileCache = null)
            => precompileCache is null
                ? new CodeInfoRepository(precompiles)
                : new CodeInfoRepository((FrozenDictionary<AddressAsKey, PrecompileInfo>)precompiles.ToFrozenDictionary(
                    kvp => kvp.Key,
                    kvp => CreateCachedPrecompile(kvp, precompileCache)));

    private static PrecompileInfo CreateCachedPrecompile(
        in KeyValuePair<AddressAsKey, PrecompileInfo> originalPrecompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)> cache) =>
        new PrecompileInfo(new CachedPrecompile(originalPrecompile.Key.Value, originalPrecompile.Value.Precompile!, cache));

    private sealed class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)> cache) : IPrecompile
    {
        public static Address Address => Address.Zero;
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)>.AlternateLookup<PreBlockCaches.PrecompileAltCacheKey> lookUp = cache.GetAlternateLookup<PreBlockCaches.PrecompileAltCacheKey>();

        public static string Name => "";

        public long BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, bool isCacheable)
        {
            PreBlockCaches.PrecompileAltCacheKey altKey = new(address, inputData.Span);
            if (!lookUp.TryGetValue(altKey, out (byte[], bool) result))
            {
                result = precompile.Run(inputData, releaseSpec, false);
                if (isCacheable)
                {
                    // we need to rebuild the key with data copy as the data can be changed by VM processing
                    PreBlockCaches.PrecompileCacheKey key = new(address, inputData.ToArray());
                    cache.TryAdd(key, result);
                }
            }

            return result;
        }
    }
}
