// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public class CodeInfoRepository : ICodeInfoRepository
{
    private static readonly CodeLruCache _codeCache = new();
    private readonly FrozenDictionary<AddressAsKey, PrecompileInfo> _localPrecompiles;

    public CodeInfoRepository(IPrecompileFactory precompileFactory)
    {
        _localPrecompiles = precompileFactory.GetPrecompiles();
    }

    public bool IsPrecompile(Address address, IReleaseSpec spec) =>
        spec.IsPrecompile(address) && _localPrecompiles.ContainsKey(address);

    public ICodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (IsPrecompile(codeSource, vmSpec))
        {
            return _localPrecompiles[codeSource];
        }

        ICodeInfo cachedCodeInfo = InternalGetCachedCode(worldState, codeSource, vmSpec);

        if (!cachedCodeInfo.IsEmpty && TryGetDelegatedAddress(cachedCodeInfo.CodeSpan, out delegationAddress))
        {
            if (followDelegation)
                cachedCodeInfo = InternalGetCachedCode(worldState, delegationAddress, vmSpec);
        }

        return cachedCodeInfo;
    }

    private static ICodeInfo InternalGetCachedCode(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        ref readonly ValueHash256 codeHash = ref worldState.GetCodeHash(codeSource);
        return InternalGetCachedCode(worldState, in codeHash, vmSpec);
    }

    private static ICodeInfo InternalGetCachedCode(IReadOnlyStateProvider worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        ValueHash256 codeHash = worldState.GetCodeHash(codeSource);
        return InternalGetCachedCode(worldState, in codeHash, vmSpec);
    }

    private static ICodeInfo InternalGetCachedCode(IReadOnlyStateProvider worldState, in ValueHash256 codeHash, IReleaseSpec vmSpec)
    {
        ICodeInfo? cachedCodeInfo = null;
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            cachedCodeInfo = CodeInfo.Empty;
        }

        cachedCodeInfo ??= _codeCache.Get(in codeHash);
        if (cachedCodeInfo is null)
        {
            byte[]? code = worldState.GetCode(in codeHash);

            if (code is null)
            {
                MissingCode(in codeHash);
            }

            cachedCodeInfo = CodeInfoFactory.CreateCodeInfo(code, vmSpec, ValidationStrategy.ExtractHeader);
            _codeCache.Set(in codeHash, cachedCodeInfo);
        }
        else
        {
            Metrics.IncrementCodeDbCache();
        }

        return cachedCodeInfo;

        [DoesNotReturn, StackTraceHidden]
        static void MissingCode(in ValueHash256 codeHash)
        {
            throw new DataException($"Code {codeHash} missing in the state");
        }
    }

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        // If the code is already in the cache, we don't need to create and add it again (and reanalyze it)
        if (state.InsertCode(codeOwner, in codeHash, code, spec) &&
            _codeCache.Get(in codeHash) is null)
        {
            ICodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(code, spec, ValidationStrategy.ExtractHeader);
            _codeCache.Set(in codeHash, codeInfo);
        }
    }

    public void SetDelegation(IWorldState state, Address codeSource, Address authority, IReleaseSpec spec)
    {
        if (codeSource == Address.Zero)
        {
            state.InsertCode(authority, Keccak.OfAnEmptyString, Array.Empty<byte>(), spec);
            return;
        }
        byte[] authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
        Eip7702Constants.DelegationHeader.CopyTo(authorizedBuffer);
        codeSource.Bytes.CopyTo(authorizedBuffer, Eip7702Constants.DelegationHeader.Length);
        ValueHash256 codeHash = ValueKeccak.Compute(authorizedBuffer);
        if (state.InsertCode(authority, codeHash, authorizedBuffer.AsMemory(), spec)
            // If the code is already in the cache, we don't need to create CodeInfo and add it again (and reanalyze it)
            && _codeCache.Get(in codeHash) is null)
        {
            _codeCache.Set(codeHash, new CodeInfo(authorizedBuffer));
        }
    }

    /// <summary>
    /// Retrieves code hash of delegation if delegated. Otherwise code hash of <paramref name="address"/>.
    /// </summary>
    /// <param name="worldState"></param>
    /// <param name="address"></param>
    public ValueHash256 GetExecutableCodeHash(IWorldState worldState, Address address, IReleaseSpec spec)
    {
        ValueHash256 codeHash = worldState.GetCodeHash(address);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            return Keccak.OfAnEmptyString.ValueHash256;
        }

        ICodeInfo codeInfo = InternalGetCachedCode(worldState, address, spec);
        return codeInfo.IsEmpty
            ? Keccak.OfAnEmptyString.ValueHash256
            : codeHash;
    }

    /// <remarks>
    /// Parses delegation code to extract the contained address.
    /// <b>Assumes </b><paramref name="code"/> <b>is delegation code!</b>
    /// </remarks>
    private static bool TryGetDelegatedAddress(ReadOnlySpan<byte> code, [NotNullWhen(true)] out Address? address)
    {
        if (Eip7702Constants.IsDelegatedCode(code))
        {
            address = new Address(code[Eip7702Constants.DelegationHeader.Length..].ToArray());
            return true;
        }

        address = null;
        return false;
    }

    public bool TryGetDelegation(IReadOnlyStateProvider worldState, Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        TryGetDelegatedAddress(InternalGetCachedCode(worldState, address, spec).CodeSpan, out delegatedAddress);

    public bool TryGetDelegation(IReadOnlyStateProvider worldState, in ValueHash256 codeHash, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        TryGetDelegatedAddress(InternalGetCachedCode(worldState, in codeHash, spec).CodeSpan, out delegatedAddress);

    private sealed class CodeLruCache
    {
        private const int CacheCount = 16;
        private const int CacheMax = CacheCount - 1;
        private readonly ClockCache<ValueHash256, ICodeInfo>[] _caches;

        public CodeLruCache()
        {
            _caches = new ClockCache<ValueHash256, ICodeInfo>[CacheCount];
            for (int i = 0; i < _caches.Length; i++)
            {
                // Cache per nibble to reduce contention as TxPool is very parallel
                _caches[i] = new ClockCache<ValueHash256, ICodeInfo>(MemoryAllowance.CodeCacheSize / CacheCount);
            }
        }

        public ICodeInfo? Get(in ValueHash256 codeHash)
        {
            ClockCache<ValueHash256, ICodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Get(codeHash);
        }

        public bool Set(in ValueHash256 codeHash, ICodeInfo codeInfo)
        {
            ClockCache<ValueHash256, ICodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Set(codeHash, codeInfo);
        }

        private static int GetCacheIndex(in ValueHash256 codeHash) => codeHash.Bytes[^1] & CacheMax;

        public bool TryGet(in ValueHash256 codeHash, [NotNullWhen(true)] out ICodeInfo? codeInfo)
        {
            codeInfo = Get(in codeHash);
            return codeInfo is not null;
        }
    }
}

