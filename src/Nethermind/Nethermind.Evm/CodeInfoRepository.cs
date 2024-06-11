// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Evm.Precompiles.Snarks;
using Nethermind.State;

namespace Nethermind.Evm;

public class CodeInfoRepository : ICodeInfoRepository
{
    internal sealed class CodeLruCache
    {
        private const int CacheCount = 16;
        private const int CacheMax = CacheCount - 1;
        private readonly LruCacheLowObject<ValueHash256, CodeInfo>[] _caches;

        public CodeLruCache()
        {
            _caches = new LruCacheLowObject<ValueHash256, CodeInfo>[CacheCount];
            for (int i = 0; i < _caches.Length; i++)
            {
                // Cache per nibble to reduce contention as TxPool is very parallel
                _caches[i] = new LruCacheLowObject<ValueHash256, CodeInfo>(MemoryAllowance.CodeCacheSize / CacheCount, $"VM bytecodes {i}");
            }
        }

        public CodeInfo? Get(in ValueHash256 codeHash)
        {
            LruCacheLowObject<ValueHash256, CodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Get(codeHash);
        }

        public bool Set(in ValueHash256 codeHash, CodeInfo codeInfo)
        {
            LruCacheLowObject<ValueHash256, CodeInfo> cache = _caches[GetCacheIndex(codeHash)];
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
        }.ToFrozenDictionary();
    }

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        if (codeSource.IsPrecompile(vmSpec))
        {
            return _precompiles[codeSource];
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
}
