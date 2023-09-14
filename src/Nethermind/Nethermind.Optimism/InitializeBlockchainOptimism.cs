// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism : InitializeBlockchain
{
    private readonly OptimismNethermindApi _api;
    private readonly IBlocksConfig _blocksConfig;

    public InitializeBlockchainOptimism(OptimismNethermindApi api) : base(api)
    {
        _api = api;
        _blocksConfig = api.Config<IBlocksConfig>();
    }

    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        if (_api.InvalidChainTracker is null) throw new StepDependencyException(nameof(_api.InvalidChainTracker));
        if (_api.BlockchainProcessor is null) throw new StepDependencyException(nameof(_api.BlockchainProcessor));

        // because we know we are in Optimism and it was OptimismPlugin that created the InvalidChainTracker
        ((InvalidChainTracker)_api.InvalidChainTracker).SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor);
    }

    protected override ITransactionProcessor CreateTransactionProcessor()
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));

        VirtualMachine virtualMachine = CreateVirtualMachine();

        Address l1FeeRecipient = new("0x420000000000000000000000000000000000001A");

        OPL1CostHelper l1CostHelper = new();
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

    protected override IHeaderValidator CreateHeaderValidator()
    {
        if (_api.InvalidChainTracker is null) throw new StepDependencyException(nameof(_api.InvalidChainTracker));

        OptimismHeaderValidator opHeaderValidator = new(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);

        return new InvalidHeaderInterceptor(opHeaderValidator, _api.InvalidChainTracker, _api.LogManager);
    }

    protected override IBlockValidator CreateBlockValidator()
    {
        if (_api.InvalidChainTracker is null) throw new StepDependencyException(nameof(_api.InvalidChainTracker));
        if (_api.TxValidator is null) throw new StepDependencyException(nameof(_api.TxValidator));

        OptimismTxValidator txValidator = new(_api.TxValidator);
        BlockValidator blockValidator = new(
            txValidator,
            _api.HeaderValidator,
            _api.UnclesValidator,
            _api.SpecProvider,
            _api.LogManager);

        return new InvalidBlockInterceptor(blockValidator, _api.InvalidChainTracker, _api.LogManager);
    }

    protected override IUnclesValidator CreateUnclesValidator() => Always.Valid;

    protected override IHealthHintService CreateHealthHintService() =>
        new ManualHealthHintService(_blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint);

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;
}
