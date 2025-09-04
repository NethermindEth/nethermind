// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;

namespace Nethermind.Blockchain;

public class EthereumPrecompileFactory() : IPrecompileFactory
{
    private static FrozenDictionary<AddressAsKey, PrecompileInfo> Precompiles
    {
        get => new Dictionary<AddressAsKey, PrecompileInfo>
        {
            [EcRecoverPrecompile.Address] = new(EcRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance),

            [BN254AddPrecompile.Address] = new(BN254AddPrecompile.Instance),
            [BN254MulPrecompile.Address] = new(BN254MulPrecompile.Instance),
            [BN254PairingPrecompile.Address] = new(BN254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new(Blake2FPrecompile.Instance),

            [G1AddPrecompile.Address] = new(G1AddPrecompile.Instance),
            [G1MSMPrecompile.Address] = new(G1MSMPrecompile.Instance),
            [G2AddPrecompile.Address] = new(G2AddPrecompile.Instance),
            [G2MSMPrecompile.Address] = new(G2MSMPrecompile.Instance),
            [PairingCheckPrecompile.Address] = new(PairingCheckPrecompile.Instance),
            [MapFpToG1Precompile.Address] = new(MapFpToG1Precompile.Instance),
            [MapFp2ToG2Precompile.Address] = new(MapFp2ToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new(PointEvaluationPrecompile.Instance),

            [Secp256r1Precompile.Address] = new(Secp256r1Precompile.Instance),

            [L1SloadPrecompile.Address] = new(L1SloadPrecompile.Instance),
        }.ToFrozenDictionary();
    }

    public IDictionary<AddressAsKey, PrecompileInfo> CreatePrecompiles()
    {
        return Precompiles;
    }
}
