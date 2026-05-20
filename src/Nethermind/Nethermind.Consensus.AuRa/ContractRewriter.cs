// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.AuRa;

public class ContractRewriter(
    IDictionary<long, IDictionary<Address, byte[]>> contractOverrides,
    (ulong, Address, byte[])[] contractOverridesTimestamp)
{
    private readonly IDictionary<long, IDictionary<Address, byte[]>> _contractOverrides = contractOverrides;
    private readonly (ulong, Address, byte[])[] _contractOverridesTimestamp = contractOverridesTimestamp;

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
        foreach ((ulong Timestamp, Address Address, byte[] Code) codeOverride in _contractOverridesTimestamp)
        {
            if (timestamp >= codeOverride.Timestamp && parentTimestamp < codeOverride.Timestamp)
            {
                InsertOverwriteCode(codeOverride.Address, codeOverride.Code, stateProvider, spec);
                result = true;
            }
        }
        return result;
    }

    private static bool InsertOverwriteCode(IDictionary<Address, byte[]> overrides, IWorldState stateProvider, IReleaseSpec spec)
    {
        bool result = false;
        foreach (KeyValuePair<Address, byte[]> contractOverride in overrides)
        {
            InsertOverwriteCode(contractOverride.Key, contractOverride.Value, stateProvider, spec);
            result = true;
        }
        return result;
    }

    private static void InsertOverwriteCode(Address address, byte[] code, IWorldState stateProvider, IReleaseSpec spec)
    {
        stateProvider.CreateAccountIfNotExists(address, 0, 0);
        stateProvider.InsertCode(address, code, spec);
    }
}
