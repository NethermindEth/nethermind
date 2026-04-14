// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Blockchain;

public class EthereumPrecompileProvider() : IPrecompileProvider
{
    private static FrozenDictionary<AddressAsKey, CodeInfo> Precompiles
    {
        get => new Dictionary<AddressAsKey, CodeInfo>
        {
            [ECRecoverPrecompile.Address] = new(ECRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance),

            [BN254AddPrecompile.Address] = new(BN254AddPrecompile.Instance),
            [BN254MulPrecompile.Address] = new(BN254MulPrecompile.Instance),
            [BN254PairingPrecompile.Address] = new(BN254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new(Blake2FPrecompile.Instance),

            [Bls12381G1AddPrecompile.Address] = new(Bls12381G1AddPrecompile.Instance),
            [Bls12381G1MsmPrecompile.Address] = new(Bls12381G1MsmPrecompile.Instance),
            [Bls12381G2AddPrecompile.Address] = new(Bls12381G2AddPrecompile.Instance),
            [Bls12381G2MsmPrecompile.Address] = new(Bls12381G2MsmPrecompile.Instance),
            [Bls12381PairingCheckPrecompile.Address] = new(Bls12381PairingCheckPrecompile.Instance),
            [Bls12381FpToG1Precompile.Address] = new(Bls12381FpToG1Precompile.Instance),
            [Bls12381Fp2ToG2Precompile.Address] = new(Bls12381Fp2ToG2Precompile.Instance),

            [KzgPointEvaluationPrecompile.Address] = new(KzgPointEvaluationPrecompile.Instance),

            [SecP256r1Precompile.Address] = new(SecP256r1Precompile.Instance),

            [L1SloadPrecompile.Address] = new(L1SloadPrecompile.Instance),
        }.ToFrozenDictionary();
    }

    public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => Precompiles;
}
