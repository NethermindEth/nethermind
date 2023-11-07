// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.InvalidChainTracker;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism : InitializeBlockchain
{
    private readonly OptimismNethermindApi _api;

    public InitializeBlockchainOptimism(OptimismNethermindApi api) : base(api)
    {
        _api = api;
    }

    protected override Task InitBlockchain()
    {
        _api.SpecHelper = _api.Container.Resolve<OPSpecHelper>();
        _api.L1CostHelper = _api.Container.Resolve<OPL1CostHelper>();

        return base.InitBlockchain();
    }

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;
}
