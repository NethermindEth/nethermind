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
using Nethermind.Int256;
using Nethermind.Crypto;
using System.Linq;

namespace Nethermind.Evm;

public class CodeInfoRepository : ICodeInfoRepository
{
    internal sealed class CodeLruCache
    {
        private const int CacheCount = 16;
        private const int CacheMax = CacheCount - 1;
        private readonly ClockCache<ValueHash256, CodeInfo>[] _caches;

        public CodeLruCache()
        {
            _caches = new ClockCache<ValueHash256, CodeInfo>[CacheCount];
            for (int i = 0; i < _caches.Length; i++)
            {
                // Cache per nibble to reduce contention as TxPool is very parallel
                _caches[i] = new ClockCache<ValueHash256, CodeInfo>(MemoryAllowance.CodeCacheSize / CacheCount);
            }
        }

        public CodeInfo? Get(in ValueHash256 codeHash)
        {
            ClockCache<ValueHash256, CodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Get(codeHash);
        }

        public bool Set(in ValueHash256 codeHash, CodeInfo codeInfo)
        {
            ClockCache<ValueHash256, CodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Set(codeHash, codeInfo);
        }

        private static int GetCacheIndex(in ValueHash256 codeHash) => codeHash.Bytes[^1] & CacheMax;

        public bool TryGet(in ValueHash256 codeHash, [NotNullWhen(true)] out CodeInfo? codeInfo)
        {
            codeInfo = Get(codeHash);
            return codeInfo is not null;
        }
    }


    private static readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles = InitializePrecompiledContracts();
    private static readonly CodeLruCache _codeCache = new();
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _localPrecompiles;
    private readonly EthereumEcdsa _ethereumEcdsa;

    private static FrozenDictionary<AddressAsKey, CodeInfo> InitializePrecompiledContracts()
    {
        return new Dictionary<AddressAsKey, CodeInfo>
        {
            [EcRecoverPrecompile.Address] = new(EcRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance),

            [Bn254AddPrecompile.Address] = new(Bn254AddPrecompile.Instance),
            [Bn254MulPrecompile.Address] = new(Bn254MulPrecompile.Instance),
            [Bn254PairingPrecompile.Address] = new(Bn254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new(Blake2FPrecompile.Instance),

            [G1AddPrecompile.Address] = new(G1AddPrecompile.Instance),
            [G1MulPrecompile.Address] = new(G1MulPrecompile.Instance),
            [G1MultiExpPrecompile.Address] = new(G1MultiExpPrecompile.Instance),
            [G2AddPrecompile.Address] = new(G2AddPrecompile.Instance),
            [G2MulPrecompile.Address] = new(G2MulPrecompile.Instance),
            [G2MultiExpPrecompile.Address] = new(G2MultiExpPrecompile.Instance),
            [PairingPrecompile.Address] = new(PairingPrecompile.Instance),
            [MapToG1Precompile.Address] = new(MapToG1Precompile.Instance),
            [MapToG2Precompile.Address] = new(MapToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new(PointEvaluationPrecompile.Instance),

            [Secp256r1Precompile.Address] = new(Secp256r1Precompile.Instance),
        }.ToFrozenDictionary();
    }

    public CodeInfoRepository(ulong chainId, ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)>? precompileCache = null)
    {
        _ethereumEcdsa = new EthereumEcdsa(chainId);
        _localPrecompiles = precompileCache is null
            ? _precompiles
            : _precompiles.ToFrozenDictionary(kvp => kvp.Key, kvp => CreateCachedPrecompile(kvp, precompileCache));        
    }

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        if (codeSource.IsPrecompile(vmSpec))
        {
            return _localPrecompiles[codeSource];
        }

        CodeInfo? cachedCodeInfo = null;
        ValueHash256 codeHash = worldState.GetCodeHash(codeSource);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            cachedCodeInfo = CodeInfo.Empty;
        }

        cachedCodeInfo ??= _codeCache.Get(codeHash);
        if (cachedCodeInfo is null)
        {
            byte[]? code = worldState.GetCode(codeHash);

            if (HasDelegatedCode(code))
            {
                code = worldState.GetCode(ParseDelegatedAddress(code));
            }

            if (code is null)
            {
                MissingCode(codeSource, codeHash);
            }
            
            cachedCodeInfo = new CodeInfo(code);
            cachedCodeInfo.AnalyseInBackgroundIfRequired();
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

    public CodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode)
    {
        if (!_codeCache.TryGet(codeHash, out CodeInfo? codeInfo))
        {
            codeInfo = new(initCode.ToArray());

            // Prime the code cache as likely to be used by more txs
            _codeCache.Set(codeHash, codeInfo);
        }

        return codeInfo;
    }

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        CodeInfo codeInfo = new(code);
        codeInfo.AnalyseInBackgroundIfRequired();

        Hash256 codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);
        state.InsertCode(codeOwner, codeHash, code, spec);
        _codeCache.Set(codeHash, codeInfo);
    }

    /// <summary>
    /// Insert code delegations from transaction authorization_list authorized by signature,
    /// and return all authority addresses that was accessed.
    /// eip-7702
    /// </summary>
    public CodeInsertResult InsertFromAuthorizations(
        IWorldState worldState,
        AuthorizationTuple?[] authorizations,
        IReleaseSpec spec)
    {
        List<Address> result = new();
        int refunds = 0;
        //TODO optimize
        foreach (AuthorizationTuple? authTuple in authorizations)
        {
            if (authTuple is null)
                continue;
            authTuple.Authority = authTuple.Authority ?? _ethereumEcdsa.RecoverAddress(authTuple);
            string? error;

            if (!result.Contains(authTuple.Authority))
                result.Add(authTuple.Authority);

            if (!IsValidForExecution(authTuple, worldState, _ethereumEcdsa.ChainId, spec, out error))
                continue;

            InsertAuthorizedCode(worldState, authTuple.CodeAddress, authTuple.Authority, spec);

            if (!worldState.AccountExists(authTuple.Authority))
                worldState.CreateAccount(authTuple.Authority, 0);
            else
                refunds++;

            worldState.IncrementNonce(authTuple.Authority);
        }
        return new CodeInsertResult(result, refunds);

        void InsertAuthorizedCode(IWorldState state, Address codeSource, Address authority, IReleaseSpec spec)
        {
            byte[] authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
            codeSource.Bytes.CopyTo(authorizedBuffer, Eip7702Constants.DelegationHeader.Length);
            Hash256 codeHash = Keccak.Compute(authorizedBuffer);
            state.InsertCode(authority, codeHash, authorizedBuffer.AsMemory(), spec);
            _codeCache.Set(codeHash, new CodeInfo(authorizedBuffer));
        }
    }

    /// <summary>
    /// Determines if a <see cref="AuthorizationTuple"/> is wellformed according to spec.
    /// </summary>
    private bool IsValidForExecution(
        AuthorizationTuple authorizationTuple,
        IWorldState stateProvider,
        ulong chainId,
        IReleaseSpec spec,
        [NotNullWhen(false)] out string? error)
    {
        if (authorizationTuple.Authority is null)
        {
            error = "Bad signature.";
            return false;
        }
        if (authorizationTuple.ChainId != 0 && chainId != authorizationTuple.ChainId)
        {
            error = $"Chain id ({authorizationTuple.ChainId}) does not match.";
            return false;
        }
        if (stateProvider.HasCode(authorizationTuple.Authority)
         && !HasDelegatedCode(stateProvider, authorizationTuple.Authority))
        {
            error = $"Authority ({authorizationTuple.Authority}) has code deployed.";
            return false;
        }
        UInt256 authNonce = stateProvider.GetNonce(authorizationTuple.Authority);
        if (authorizationTuple.Nonce is not null && authNonce != authorizationTuple.Nonce)
        {
            error = $"Skipping tuple in authorization_list because nonce is set to {authorizationTuple.Nonce}, but authority ({authorizationTuple.Authority}) has {authNonce}.";
            return false;
        }

        error = null;
        return true;
    }

    private bool HasDelegatedCode(IWorldState worldState, Address source)
    {
        return
            HasDelegatedCode(worldState.GetCode(source));
    }

    private static bool HasDelegatedCode(ReadOnlySpan<byte> code)
    {
        return
            code.Length >= Eip7702Constants.DelegationHeader.Length
            && Eip7702Constants.DelegationHeader.SequenceEqual(
                code.Slice(0, Eip7702Constants.DelegationHeader.Length));
    }

    private static Address ParseDelegatedAddress(byte[] code)
    {
        if (code.Length != Eip7702Constants.DelegationHeader.Length + Address.Size)
            throw new ArgumentException("Not valid delegation code.", nameof(code));
        return new Address(code.Skip(Eip7702Constants.DelegationHeader.Length).ToArray());
    }

    private CodeInfo CreateCachedPrecompile(
        in KeyValuePair<AddressAsKey, CodeInfo> originalPrecompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> cache) =>
        new(new CachedPrecompile(originalPrecompile.Key.Value, originalPrecompile.Value.Precompile!, cache));

    private class CachedPrecompile(
        Address address,
        IPrecompile precompile,
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> cache) : IPrecompile
    {
        public static Address Address => Address.Zero;

        public long BaseGasCost(IReleaseSpec releaseSpec) => precompile.BaseGasCost(releaseSpec);

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => precompile.DataGasCost(inputData, releaseSpec);

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
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
}
public readonly struct CodeInsertResult
{
    public CodeInsertResult(IEnumerable<Address> addresses, int refunds)
    {
        Addresses = addresses;
        Refunds = refunds;
    }
    public CodeInsertResult()
    {
        Addresses = Array.Empty<Address>();
    }
    public readonly IEnumerable<Address> Addresses;
    public readonly int Refunds;
}

