// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Evm.Precompiles.Snarks;
using Nethermind.State;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Crypto;

namespace Nethermind.Evm;

public class CodeInfoRepository : ICodeInfoRepository
{
    private static readonly FrozenDictionary<AddressAsKey, ICodeInfo> _precompiles = InitializePrecompiledContracts();
    private static readonly CodeLruCache _codeCache = new();
    private readonly FrozenDictionary<AddressAsKey, ICodeInfo> _localPrecompiles;

    private static FrozenDictionary<AddressAsKey, ICodeInfo> InitializePrecompiledContracts()
    {
        return new Dictionary<AddressAsKey, ICodeInfo>
        {
            [EcRecoverPrecompile.Address] = new CodeInfo(EcRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new CodeInfo(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new CodeInfo(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new CodeInfo(IdentityPrecompile.Instance),

            [Bn254AddPrecompile.Address] = new CodeInfo(Bn254AddPrecompile.Instance),
            [Bn254MulPrecompile.Address] = new CodeInfo(Bn254MulPrecompile.Instance),
            [Bn254PairingPrecompile.Address] = new CodeInfo(Bn254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new CodeInfo(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new CodeInfo(Blake2FPrecompile.Instance),

            [G1AddPrecompile.Address] = new CodeInfo(G1AddPrecompile.Instance),
            [G1MulPrecompile.Address] = new CodeInfo(G1MulPrecompile.Instance),
            [G1MultiMulPrecompile.Address] = new CodeInfo(G1MultiMulPrecompile.Instance),
            [G2AddPrecompile.Address] = new CodeInfo(G2AddPrecompile.Instance),
            [G2MulPrecompile.Address] = new CodeInfo(G2MulPrecompile.Instance),
            [G2MultiMulPrecompile.Address] = new CodeInfo(G2MultiMulPrecompile.Instance),
            [PairingPrecompile.Address] = new CodeInfo(PairingPrecompile.Instance),
            [MapToG1Precompile.Address] = new CodeInfo(MapToG1Precompile.Instance),
            [MapToG2Precompile.Address] = new CodeInfo(MapToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new CodeInfo(PointEvaluationPrecompile.Instance),

            [Secp256r1Precompile.Address] = new CodeInfo(Secp256r1Precompile.Instance),
        }.ToFrozenDictionary();
    }

    public CodeInfoRepository(ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)>? precompileCache = null)
    {
        _localPrecompiles = precompileCache is null
            ? _precompiles
            : _precompiles.ToFrozenDictionary(kvp => kvp.Key, kvp => CreateCachedPrecompile(kvp, precompileCache));
    }

    public ICodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (codeSource.IsPrecompile(vmSpec))
        {
            return _localPrecompiles[codeSource];
        }

        ICodeInfo cachedCodeInfo = InternalGetCachedCode(worldState, codeSource, vmSpec);

        if (TryGetDelegatedAddress(cachedCodeInfo.MachineCode.Span, out delegationAddress))
        {
            cachedCodeInfo = InternalGetCachedCode(worldState, delegationAddress, vmSpec);
        }

        return cachedCodeInfo;
    }

    private static ICodeInfo InternalGetCachedCode(IReadOnlyStateProvider worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        ICodeInfo? cachedCodeInfo = null;
        ValueHash256 codeHash = worldState.GetCodeHash(codeSource);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            cachedCodeInfo = CodeInfo.Empty;
        }

        cachedCodeInfo ??= _codeCache.Get(codeHash);
        if (cachedCodeInfo is null)
        {
            byte[]? code = worldState.GetCode(codeHash);

            if (code is null)
            {
                MissingCode(codeSource, codeHash);
            }

            cachedCodeInfo = CodeInfoFactory.CreateCodeInfo(code, vmSpec, Nethermind.Evm.EvmObjectFormat.ValidationStrategy.ExractHeader);
            _codeCache.Set(codeHash, cachedCodeInfo);
        }
        else
        {
            Db.Metrics.IncrementCodeDbCache();
        }

        return cachedCodeInfo;

        [DoesNotReturn]
        [StackTraceHidden]
        static void MissingCode(Address codeSource, in ValueHash256 codeHash)
        {
            throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
        }
    }

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        ICodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(code, spec, ValidationStrategy.ExractHeader);
        codeInfo.AnalyseInBackgroundIfRequired();

        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        state.InsertCode(codeOwner, codeHash, code, spec);
        _codeCache.Set(codeHash, codeInfo);
    }

    public void SetDelegation(IWorldState state, Address codeSource, Address authority, IReleaseSpec spec)
    {
        byte[] authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
        Eip7702Constants.DelegationHeader.CopyTo(authorizedBuffer);
        codeSource.Bytes.CopyTo(authorizedBuffer, Eip7702Constants.DelegationHeader.Length);
        ValueHash256 codeHash = ValueKeccak.Compute(authorizedBuffer);
        state.InsertCode(authority, codeHash, authorizedBuffer.AsMemory(), spec);
        _codeCache.Set(codeHash, new CodeInfo(authorizedBuffer));
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
            : TryGetDelegatedAddress(codeInfo.MachineCode.Span, out Address? delegationAddress)
                ? worldState.GetCodeHash(delegationAddress)
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
            address = new Address(code.Slice(Eip7702Constants.DelegationHeader.Length).ToArray());
            return true;
        }

        address = null;
        return false;
    }

    private ICodeInfo CreateCachedPrecompile(
        in KeyValuePair<AddressAsKey, ICodeInfo> originalPrecompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> cache) =>
        new CodeInfo(new CachedPrecompile(originalPrecompile.Key.Value, originalPrecompile.Value.Precompile!, cache));

    public bool TryGetDelegation(IReadOnlyStateProvider worldState, Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        TryGetDelegatedAddress(InternalGetCachedCode(worldState, address, spec).MachineCode.Span, out delegatedAddress);

    private class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> cache) : IPrecompile
    {
        public static Address Address => Address.Zero;

        public long BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            PreBlockCaches.PrecompileCacheKey key = new(address, inputData);
            if (!cache.TryGetValue(key, out (ReadOnlyMemory<byte>, bool) result))
            {
                result = precompile.Run(inputData, releaseSpec);
                // we need to rebuild the key with data copy as the data can be changed by VM processing
                key = new PreBlockCaches.PrecompileCacheKey(address, inputData.ToArray());
                cache.TryAdd(key, result);
            }

            return result;
        }
    }

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
            codeInfo = Get(codeHash);
            return codeInfo is not null;
        }
    }
}

