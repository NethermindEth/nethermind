// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa;

public class ContractRewriter
{
    private readonly IDictionary<long, IDictionary<Address, byte[]>> _contractOverrides;

    public ContractRewriter(IDictionary<long, IDictionary<Address, byte[]>> contractOverrides)
    {
        _contractOverrides = contractOverrides;
    }

    public void RewriteContracts(long blockNumber, IWorldState stateProvider, IReleaseSpec spec)
    {
        if (_contractOverrides.TryGetValue(blockNumber, out IDictionary<Address, byte[]> overrides))
        {
            foreach (KeyValuePair<Address, byte[]> contractOverride in overrides)
            {
                Keccak codeHash = stateProvider.UpdateCode(contractOverride.Value);
                stateProvider.UpdateCodeHash(contractOverride.Key, codeHash, spec);
            }
        }
    }
}
