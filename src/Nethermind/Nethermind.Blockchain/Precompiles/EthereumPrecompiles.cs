// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Blockchain.Precompiles.Bls;
using Nethermind.Blockchain.Precompiles.Snarks;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Blockchain.Precompiles;

public class EthereumPrecompiles
{
    public static FrozenDictionary<AddressAsKey, PrecompileInfo> Default
    {
        get => new Dictionary<AddressAsKey, PrecompileInfo>
        {
            [EcRecoverPrecompile.Address] = new PrecompileInfo(EcRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new PrecompileInfo(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new PrecompileInfo(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new PrecompileInfo(IdentityPrecompile.Instance),

            [Bn254AddPrecompile.Address] = new PrecompileInfo(Bn254AddPrecompile.Instance),
            [Bn254MulPrecompile.Address] = new PrecompileInfo(Bn254MulPrecompile.Instance),
            [Bn254PairingPrecompile.Address] = new PrecompileInfo(Bn254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new PrecompileInfo(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new PrecompileInfo(Blake2FPrecompile.Instance),

            [G1AddPrecompile.Address] = new PrecompileInfo(G1AddPrecompile.Instance),
            [G1MSMPrecompile.Address] = new PrecompileInfo(G1MSMPrecompile.Instance),
            [G2AddPrecompile.Address] = new PrecompileInfo(G2AddPrecompile.Instance),
            [G2MSMPrecompile.Address] = new PrecompileInfo(G2MSMPrecompile.Instance),
            [PairingCheckPrecompile.Address] = new PrecompileInfo(PairingCheckPrecompile.Instance),
            [MapFpToG1Precompile.Address] = new PrecompileInfo(MapFpToG1Precompile.Instance),
            [MapFp2ToG2Precompile.Address] = new PrecompileInfo(MapFp2ToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new PrecompileInfo(PointEvaluationPrecompile.Instance),

            [Secp256r1Precompile.Address] = new PrecompileInfo(Secp256r1Precompile.Instance),
        }.ToFrozenDictionary();
    }
}
