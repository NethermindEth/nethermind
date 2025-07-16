// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Evm.Precompiles.Snarks;
using System;
using System.Collections.Generic;

namespace Nethermind.Evm.Precompiles;

public static class Extensions
{
    public static void PrepareEthInput(this ReadOnlyMemory<byte> inputData, Span<byte> inputDataSpan)
    {
        inputData.Span[..Math.Min(inputDataSpan.Length, inputData.Length)]
            .CopyTo(inputDataSpan[..Math.Min(inputDataSpan.Length, inputData.Length)]);
    }

    public static OrderedDictionary<Address, string> ListPrecompiles(this IReleaseSpec spec)
    {
        OrderedDictionary<Address, string> precompiles = [];

        AddPrecompile<EcRecoverPrecompile>();
        AddPrecompile<Sha256Precompile>();
        AddPrecompile<Ripemd160Precompile>();
        AddPrecompile<IdentityPrecompile>();

        if (spec.ModExpEnabled)
        {
            AddPrecompile<ModExpPrecompile>();
        }

        if (spec.Bn128Enabled)
        {
            AddPrecompile<Bn254AddPrecompile>();
            AddPrecompile<Bn254MulPrecompile>();
            AddPrecompile<Bn254PairingPrecompile>();
        }

        if (spec.BlakeEnabled)
        {
            AddPrecompile<Blake2FPrecompile>();
        }

        if (spec.IsEip4844Enabled)
        {
            AddPrecompile<PointEvaluationPrecompile>();
        }

        if (spec.Bls381Enabled)
        {
            AddPrecompile<G1AddPrecompile>();
            AddPrecompile<G1MSMPrecompile>();
            AddPrecompile<G2AddPrecompile>();
            AddPrecompile<G2MSMPrecompile>();
            AddPrecompile<PairingCheckPrecompile>();
            AddPrecompile<MapFpToG1Precompile>();
            AddPrecompile<MapFp2ToG2Precompile>();
        }

        if (spec.IsEip7951Enabled)
        {
            AddPrecompile<Secp256r1Precompile>();
        }

        return precompiles;

        void AddPrecompile<T>() where T : IPrecompile<T> => precompiles[T.Address] = T.Name;
    }

    public static OrderedDictionary<string, Address> ListSystemContracts(this IReleaseSpec spec)
    {
        OrderedDictionary<string, Address> systemContracts = [];

        if (spec.IsBeaconBlockRootAvailable) systemContracts[Eip4788Constants.ContractAddressKey] = Eip4788Constants.BeaconRootsAddress;
        if (spec.ConsolidationRequestsEnabled) systemContracts[Eip7251Constants.ContractAddressKey] = Eip7251Constants.ConsolidationRequestPredeployAddress;
        if (spec.DepositsEnabled) systemContracts[Eip6110Constants.ContractAddressKey] = spec.DepositContractAddress;
        if (spec.IsEip2935Enabled) systemContracts[Eip2935Constants.ContractAddressKey] = Eip2935Constants.BlockHashHistoryAddress;
        if (spec.WithdrawalRequestsEnabled) systemContracts[Eip7002Constants.ContractAddressKey] = Eip7002Constants.WithdrawalRequestPredeployAddress;

        return systemContracts;
    }
}
