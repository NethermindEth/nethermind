// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    private static readonly Dictionary<Address, CodeInfo>? _precompiles;
    private static readonly LruCache<ValueKeccak, CodeInfo> _codeCache = new(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");

    static CodeInfoRepository()
    {
        _precompiles = new Dictionary<Address, CodeInfo>
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
        };
    }

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        if (codeSource.IsPrecompile(vmSpec))
        {
            if (_precompiles is null)
            {
                throw new InvalidOperationException("EVM precompile have not been initialized properly.");
            }

            return _precompiles[codeSource];
        }

        Keccak codeHash = worldState.GetCodeHash(codeSource);
        CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
        if (cachedCodeInfo is null)
        {
            byte[] code = worldState.GetCode(codeHash);

            if (code is null)
            {
                throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
            }

            cachedCodeInfo = new CodeInfo(code);
            _codeCache.Set(codeHash, cachedCodeInfo);
        }
        else
        {
            // need to touch code so that any collectors that track database access are informed
            worldState.TouchCode(codeHash);
        }

        return cachedCodeInfo;
    }

    public CodeInfo GetOrAdd(ValueKeccak codeHash, Span<byte> initCode)
    {
        if (!_codeCache.TryGet(codeHash, out CodeInfo codeInfo))
        {
            codeInfo = new(initCode.ToArray());

            // Prime the code cache as likely to be used by more txs
            _codeCache.Set(codeHash, codeInfo);
        }

        return codeInfo;
    }
}
