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
    private static readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles;
    private static readonly LruCache<ValueHash256, CodeInfo> _codeCache = new(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");

    static CodeInfoRepository()
    {
        _precompiles = InitializePrecompiledContracts();
    }

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

        CodeInfo cachedCodeInfo = null;
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
            _codeCache.Set(codeHash, cachedCodeInfo);
        }
        else
        {
            Db.Metrics.CodeDbCache++;
            // need to touch code so that any collectors that track database access are informed
            worldState.TouchCode(codeHash);
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
        if (!_codeCache.TryGet(codeHash, out CodeInfo codeInfo))
        {
            codeInfo = new(initCode.ToArray());

            // Prime the code cache as likely to be used by more txs
            _codeCache.Set(codeHash, codeInfo);
        }

        return codeInfo;
    }


    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        var codeInfo = new CodeInfo(code);
        // Start generating the JumpDestinationBitmap in background.
        ThreadPool.UnsafeQueueUserWorkItem(codeInfo, preferLocal: false);

        Hash256 codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);
        state.InsertCode(codeOwner, codeHash, code, spec);
        _codeCache.Set(codeHash, codeInfo);
    }
}
