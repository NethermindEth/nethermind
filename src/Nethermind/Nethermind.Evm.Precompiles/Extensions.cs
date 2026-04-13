// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using System.Collections.Generic;

namespace Nethermind.Evm.Precompiles;

public static class Extensions
{
    public static OrderedDictionary<string, Address> ListPrecompiles(this IReleaseSpec spec)
    {
        OrderedDictionary<string, Address> precompiles = [];

        AddPrecompile<ECRecoverPrecompile>();
        AddPrecompile<Sha256Precompile>();
        AddPrecompile<Ripemd160Precompile>();
        AddPrecompile<IdentityPrecompile>();

        if (spec.ModExpEnabled)
        {
            AddPrecompile<ModExpPrecompile>();
        }

        if (spec.BN254Enabled)
        {
            AddPrecompile<BN254AddPrecompile>();
            AddPrecompile<BN254MulPrecompile>();
            AddPrecompile<BN254PairingPrecompile>();
        }

        if (spec.BlakeEnabled)
        {
            AddPrecompile<Blake2FPrecompile>();
        }

        if (spec.IsEip4844Enabled)
        {
            AddPrecompile<KzgPointEvaluationPrecompile>();
        }

        if (spec.Bls12381Enabled)
        {
            AddPrecompile<Bls12381G1AddPrecompile>();
            AddPrecompile<Bls12381G1MsmPrecompile>();
            AddPrecompile<Bls12381G2AddPrecompile>();
            AddPrecompile<Bls12381G2MsmPrecompile>();
            AddPrecompile<Bls12381PairingCheckPrecompile>();
            AddPrecompile<Bls12381FpToG1Precompile>();
            AddPrecompile<Bls12381Fp2ToG2Precompile>();
        }

        if (spec.IsEip7951Enabled)
        {
            AddPrecompile<SecP256r1Precompile>();
        }

        if (spec.IsRip7728Enabled)
        {
            AddPrecompile<L1SloadPrecompile>();
        }

        return precompiles;

        void AddPrecompile<T>() where T : IPrecompile<T> => precompiles[T.Name] = T.Address;
    }

    public static OrderedDictionary<string, Address> ListSystemContracts(this IReleaseSpec spec)
    {
        OrderedDictionary<string, Address> systemContracts = [];

        if (spec.IsBeaconBlockRootAvailable) systemContracts[Eip4788Constants.ContractAddressKey] = Eip4788Constants.BeaconRootsAddress;
        if (spec.ConsolidationRequestsEnabled) systemContracts[Eip7251Constants.ContractAddressKey] = Eip7251Constants.ConsolidationRequestPredeployAddress;
        if (spec.DepositsEnabled) systemContracts[Eip6110Constants.ContractAddressKey] = spec.DepositContractAddress!;
        if (spec.IsEip2935Enabled) systemContracts[Eip2935Constants.ContractAddressKey] = Eip2935Constants.BlockHashHistoryAddress;
        if (spec.WithdrawalRequestsEnabled) systemContracts[Eip7002Constants.ContractAddressKey] = Eip7002Constants.WithdrawalRequestPredeployAddress;

        return systemContracts;
    }
}
