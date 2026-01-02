// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuraGenesisPostProcessor(ChainSpec chainSpec, IWorldState worldState) : IGenesisPostProcessor
{
    public void PostProcess(Block genesis)
    {
        if (chainSpec.Allocations is null) return;
        bool hasConstructorAllocation = chainSpec.Allocations.Values.Any(static a => a.Constructor is not null);
        if (hasConstructorAllocation)
        {
            worldState.CreateAccount(Address.Zero, UInt256.Zero);
            worldState.Commit(Homestead.Instance);
        }
    }
}
