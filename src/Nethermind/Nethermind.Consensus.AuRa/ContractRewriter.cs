// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa;

public class AuraContractRewriter
{
    private readonly IDictionary<long, IDictionary<Address, byte[]>> _contractOverrides;

    public AuraContractRewriter(IDictionary<long, IDictionary<Address, byte[]>> contractOverrides)
    {
        _contractOverrides = contractOverrides;
    }

    public void RewriteContracts(long blockNumber, IWorldState stateProvider, IReleaseSpec spec)
    {
        if (_contractOverrides.TryGetValue(blockNumber, out IDictionary<Address, byte[]> overrides))
        {
            foreach (KeyValuePair<Address, byte[]> contractOverride in overrides)
            {
                stateProvider.InsertCode(contractOverride.Key, contractOverride.Value, spec);
            }
        }
    }
}
