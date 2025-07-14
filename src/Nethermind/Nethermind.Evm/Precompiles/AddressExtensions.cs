// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Evm.Precompiles.Snarks;

namespace Nethermind.Evm.Precompiles;

public static class AddressExtensions
{
    public static bool IsPrecompile(this Address address, IReleaseSpec releaseSpec)
    {
        Span<uint> data = MemoryMarshal.Cast<byte, uint>(address.Bytes.AsSpan());
        return (data[4] & 0x0000ffff) == 0
            && data[3] == 0 && data[2] == 0 && data[1] == 0 && data[0] == 0
            && ((data[4] >>> 16) & 0xff) switch
            {
                0x00 => (data[4] >>> 24) switch
                {
                    0x01 => true,
                    0x02 => true,
                    0x03 => true,
                    0x04 => true,
                    0x05 => releaseSpec.ModExpEnabled,
                    0x06 => releaseSpec.Bn128Enabled,
                    0x07 => releaseSpec.Bn128Enabled,
                    0x08 => releaseSpec.Bn128Enabled,
                    0x09 => releaseSpec.BlakeEnabled,
                    0x0a => releaseSpec.IsEip4844Enabled,
                    0x0b => releaseSpec.Bls381Enabled,
                    0x0c => releaseSpec.Bls381Enabled,
                    0x0d => releaseSpec.Bls381Enabled,
                    0x0e => releaseSpec.Bls381Enabled,
                    0x0f => releaseSpec.Bls381Enabled,
                    0x10 => releaseSpec.Bls381Enabled,
                    0x11 => releaseSpec.Bls381Enabled,
                    _ => false
                },
                0x01 => (data[4] >>> 24) switch
                {
                    0x00 => releaseSpec.IsEip7951Enabled,
                    _ => false
                },
                _ => false
            };
    }

    public static Dictionary<Address, string> ListPrecompiles(this IReleaseSpec spec)
    {
        Dictionary<Address, string> precompiles = new();

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

    public static Dictionary<string, Address> ListSystemContracts(this IReleaseSpec spec)
    {
        Dictionary<string, Address> systemContracts = new();

        if (spec.IsBeaconBlockRootAvailable) systemContracts["BEACON_ROOTS_ADDRESS"] = Eip4788Constants.BeaconRootsAddress;
        if (spec.ConsolidationRequestsEnabled) systemContracts["CONSOLIDATION_REQUEST_PREDEPLOY_ADDRESS"] = Eip7251Constants.ConsolidationRequestPredeployAddress;
        if (spec.DepositsEnabled) systemContracts["DEPOSIT_CONTRACT_ADDRESS"] = spec.DepositContractAddress;
        if (spec.IsEip2935Enabled) systemContracts["HISTORY_STORAGE_ADDRESS"] = Eip2935Constants.BlockHashHistoryAddress;
        if (spec.WithdrawalRequestsEnabled) systemContracts["WITHDRAWAL_REQUEST_PREDEPLOY_ADDRESS"] = Eip7002Constants.WithdrawalRequestPredeployAddress;

        return systemContracts;
    }
}
