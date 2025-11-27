// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(TaikoNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider) : InitializeBlockchain(api, chainHeadInfoProvider, new CompliantNodeFilters([]))
{
    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => NeverStartBlockProductionPolicy.Instance;
}
