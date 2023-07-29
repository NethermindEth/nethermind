// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Consensus.Validators;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism : InitializeBlockchain
{
    private readonly INethermindApi _api;
    private readonly IOptimismConfig _opConfig;

    public InitializeBlockchainOptimism(INethermindApi api) : base(api)
    {
        _api = api;
        _opConfig = api.Config<IOptimismConfig>();
    }

    protected override ITransactionProcessor CreateTransactionProcessor()
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));

        VirtualMachine virtualMachine = CreateVirtualMachine();

        OPL1CostHelper l1CostHelper = new(_opConfig.L1FeeReceiver);
        OPConfigHelper opConfigHelper = new(_opConfig.RegolithBlockNumber, _opConfig.BedrockBlockNumber, _opConfig.L1FeeReceiver);

        return new OptimismTransactionProcessor(
            _api.SpecProvider,
            _api.WorldState,
            virtualMachine,
            _api.LogManager,
            l1CostHelper,
            opConfigHelper
        );
    }

    protected override IHeaderValidator CreateHeaderValidator()
        => new OptimismHeaderValidator(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);
}
