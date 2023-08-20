// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus;

public class ContractRewriter
{
    public void RewriteContracts(IWorldState stateProvider, IReleaseSpec spec)
    {
        if(spec.RewriteContracts is null) return;
        
        foreach (KeyValuePair<Address, byte[]> contractOverride in spec.RewriteContracts)
        {
            stateProvider.InsertCode(contractOverride.Key, contractOverride.Value, spec);
        }
    }
}
