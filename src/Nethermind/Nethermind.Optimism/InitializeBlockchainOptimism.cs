// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism : InitializeBlockchain
{
    private readonly INethermindApi _api;

    public InitializeBlockchainOptimism(INethermindApi api) : base(api)
    {
        _api = api;
    }

    protected override ITransactionProcessor CreateTransactionProcessor()
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));

        VirtualMachine virtualMachine = CreateVirtualMachine();

        Address l1FeeRecipient = new("0x420000000000000000000000000000000000001A");

        OPL1CostHelper l1CostHelper = new(l1FeeRecipient);
        OPSpecHelper opConfigHelper = new(
            _api.ChainSpec.Optimism.RegolithTimestamp,
            _api.ChainSpec.Optimism.BedrockBlockNumber,
            l1FeeRecipient // it would be good to get this last one from chainspec too
        );

        return new OptimismTransactionProcessor(
            _api.SpecProvider,
            _api.WorldState,
            virtualMachine,
            _api.LogManager,
            l1CostHelper,
            opConfigHelper
        );
    }

    protected override ITxValidator CreateTxValidator()
    {
        return new OptimismTxValidator(base.CreateTxValidator());
    }

    protected override IHeaderValidator CreateHeaderValidator()
        => new OptimismHeaderValidator(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);
}
