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
        AddPrecompile<ModExpPrecompile>();
        AddPrecompile<BN254AddPrecompile>();
        AddPrecompile<BN254MulPrecompile>();
        AddPrecompile<BN254PairingCheckPrecompile>();
        AddPrecompile<Blake2FPrecompile>();
        AddPrecompile<KzgPointEvaluationPrecompile>();
        AddPrecompile<Bls12381G1AddPrecompile>();
        AddPrecompile<Bls12381G1MsmPrecompile>();
        AddPrecompile<Bls12381G2AddPrecompile>();
        AddPrecompile<Bls12381G2MsmPrecompile>();
        AddPrecompile<Bls12381PairingCheckPrecompile>();
        AddPrecompile<Bls12381FpToG1Precompile>();
        AddPrecompile<Bls12381Fp2ToG2Precompile>();
        AddPrecompile<SecP256r1Precompile>();

        return precompiles;

        void AddPrecompile<T>() where T : IPrecompile<T>
        {
            if (spec.IsPrecompile(T.Address)) precompiles[T.Instance.Name] = T.Address;
        }
    }

    public static OrderedDictionary<string, Address> ListSystemContracts(this IReleaseSpec spec)
    {
        OrderedDictionary<string, Address> systemContracts = [];

        if (spec.IsBeaconBlockRootAvailable) systemContracts[Eip4788Constants.ContractAddressKey] = spec.Eip4788ContractAddress ?? Eip4788Constants.BeaconRootsAddress;
        if (spec.BuilderRequestsEnabled)
        {
            systemContracts[Eip8282Constants.BuilderDepositContractAddressKey] = Eip8282Constants.BuilderDepositRequestPredeployAddress;
            systemContracts[Eip8282Constants.BuilderExitContractAddressKey] = Eip8282Constants.BuilderExitRequestPredeployAddress;
        }
        if (spec.ConsolidationRequestsEnabled) systemContracts[Eip7251Constants.ContractAddressKey] = spec.Eip7251ContractAddress!;
        if (spec.DepositsEnabled) systemContracts[Eip6110Constants.ContractAddressKey] = spec.DepositContractAddress!;
        if (spec.IsEip2935Enabled) systemContracts[Eip2935Constants.ContractAddressKey] = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (spec.WithdrawalRequestsEnabled) systemContracts[Eip7002Constants.ContractAddressKey] = spec.Eip7002ContractAddress!;

        return systemContracts;
    }
}
