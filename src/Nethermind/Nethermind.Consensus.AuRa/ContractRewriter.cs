// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.AuRa;

public class ContractRewriter(
    IDictionary<long, IDictionary<Address, byte[]>> contractOverrides,
    IDictionary<ulong, IDictionary<Address, byte[]>> contractOverridesTimestamp)
{
    private readonly IDictionary<long, IDictionary<Address, byte[]>> _contractOverrides = contractOverrides;
    private readonly IDictionary<ulong, IDictionary<Address, byte[]>> _contractOverridesTimestamp = contractOverridesTimestamp;

    public bool RewriteContracts(long blockNumber, IWorldState stateProvider, IReleaseSpec spec)
    {
        bool result = false;
        if (_contractOverrides.TryGetValue(blockNumber, out IDictionary<Address, byte[]> overrides))
        {
            result = InsertOverwriteCode(overrides, stateProvider, spec);
        }
        return result;
    }

    public bool RewriteContracts(ulong timestamp, ulong parentTimestamp, IWorldState stateProvider, IReleaseSpec spec)
    {
        bool result = false;
        foreach (KeyValuePair<ulong, IDictionary<Address, byte[]>> overrides in _contractOverridesTimestamp)
        {
            if (timestamp >= overrides.Key && parentTimestamp < overrides.Key)
            {
                result &= InsertOverwriteCode(overrides.Value, stateProvider, spec);
            }
        }
        return result;
    }

    private static bool InsertOverwriteCode(IDictionary<Address, byte[]> overrides, IWorldState stateProvider, IReleaseSpec spec)
    {
        bool result = false;
        foreach (KeyValuePair<Address, byte[]> contractOverride in overrides)
        {
            stateProvider.CreateAccountIfNotExists(contractOverride.Key, 0, 0);
            stateProvider.InsertCode(contractOverride.Key, contractOverride.Value, spec);
            result = true;
        }
        return result;
    }
}
