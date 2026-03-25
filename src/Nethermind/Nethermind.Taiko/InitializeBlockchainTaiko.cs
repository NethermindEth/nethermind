// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Init.Steps;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(TaikoNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider, Lazy<ITxGossipPolicy> txGossipPolicy) : InitializeBlockchain(api, chainHeadInfoProvider, txGossipPolicy)
{
    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => NeverStartBlockProductionPolicy.Instance;
}
