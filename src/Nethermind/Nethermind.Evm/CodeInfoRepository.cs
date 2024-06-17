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
        private readonly LruCacheLowObject<ValueHash256, ICodeInfo>[] _caches;

        public CodeLruCache()
        {
            _caches = new LruCacheLowObject<ValueHash256, ICodeInfo>[CacheCount];
            for (int i = 0; i < _caches.Length; i++)
            {
                // Cache per nibble to reduce contention as TxPool is very parallel
                _caches[i] = new LruCacheLowObject<ValueHash256, ICodeInfo>(MemoryAllowance.CodeCacheSize / CacheCount, $"VM bytecodes {i}");
            }
        }

        public ICodeInfo? Get(in ValueHash256 codeHash)
        {
            LruCacheLowObject<ValueHash256, ICodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Get(codeHash);
        }

        public bool Set(in ValueHash256 codeHash, ICodeInfo codeInfo)
        {
            LruCacheLowObject<ValueHash256, ICodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Set(codeHash, codeInfo);
        }

        private static int GetCacheIndex(in ValueHash256 codeHash) => codeHash.Bytes[^1] & CacheMax;

        public bool TryGet(in ValueHash256 codeHash, [NotNullWhen(true)] out ICodeInfo? codeInfo)
        {
            codeInfo = Get(codeHash);
            return codeInfo is not null;
        }
    }


    private static readonly FrozenDictionary<AddressAsKey, ICodeInfo> _precompiles = InitializePrecompiledContracts();
    private static readonly CodeLruCache _codeCache = new();

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
            [G1MultiExpPrecompile.Address] = new CodeInfo(G1MultiExpPrecompile.Instance),
            [G2AddPrecompile.Address] = new CodeInfo(G2AddPrecompile.Instance),
            [G2MulPrecompile.Address] = new CodeInfo(G2MulPrecompile.Instance),
            [G2MultiExpPrecompile.Address] = new CodeInfo(G2MultiExpPrecompile.Instance),
            [PairingPrecompile.Address] = new CodeInfo(PairingPrecompile.Instance),
            [MapToG1Precompile.Address] = new CodeInfo(MapToG1Precompile.Instance),
            [MapToG2Precompile.Address] = new CodeInfo(MapToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new CodeInfo(PointEvaluationPrecompile.Instance),
        }.ToFrozenDictionary();
    }

    public ICodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        if (codeSource.IsPrecompile(vmSpec))
        {
            return _precompiles[codeSource];
        }

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

            cachedCodeInfo = CodeInfoFactory.CreateCodeInfo(code, vmSpec);
            if(cachedCodeInfo is CodeInfo eof0CodeInfo)
                eof0CodeInfo.AnalyseInBackgroundIfRequired();
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

    public ICodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode, IReleaseSpec spec)
    {
        if (!_codeCache.TryGet(codeHash, out ICodeInfo? codeInfo))
        {
            codeInfo = CodeInfoFactory.CreateCodeInfo(initCode.ToArray(), spec);

            // Prime the code cache as likely to be used by more txs
            _codeCache.Set(codeHash, codeInfo);
        }

        return codeInfo;
    }


    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        ICodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(code, spec);
        if(codeInfo is CodeInfo eof0CodeInfo)
                eof0CodeInfo.AnalyseInBackgroundIfRequired();

        Hash256 codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);
        state.InsertCode(codeOwner, codeHash, code, spec);
        _codeCache.Set(codeHash, codeInfo);
    }
}
