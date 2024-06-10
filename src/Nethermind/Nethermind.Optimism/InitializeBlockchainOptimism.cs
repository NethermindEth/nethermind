// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Withdrawals;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.InvalidChainTracker;

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

    protected override Task InitBlockchain()
    {
        _api.SpecHelper = new(_api.ChainSpec.Optimism);
        _api.L1CostHelper = new(_api.SpecHelper, _api.ChainSpec.Optimism.L1BlockAddress);

        return base.InitBlockchain();
    }

    protected override ITransactionProcessor CreateTransactionProcessor()
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.SpecHelper is null) throw new StepDependencyException(nameof(_api.SpecHelper));
        if (_api.L1CostHelper is null) throw new StepDependencyException(nameof(_api.L1CostHelper));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));

        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = CreateVirtualMachine(codeInfoRepository);

        return new OptimismTransactionProcessor(
            _api.SpecProvider,
            _api.WorldState,
            virtualMachine,
            _api.LogManager,
            _api.L1CostHelper,
            _api.SpecHelper,
            codeInfoRepository
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

    protected override BlockProcessor CreateBlockProcessor()
    {
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.TransactionProcessor is null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
        if (_api.SpecHelper is null) throw new StepDependencyException(nameof(_api.SpecHelper));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));

        Create2DeployerContractRewriter contractRewriter =
            new(_api.SpecHelper, _api.SpecProvider, _api.BlockTree);

        return new OptimismBlockProcessor(
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource.Get(_api.TransactionProcessor!),
            new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor, _api.WorldState),
            _api.WorldState,
            _api.ReceiptStorage,
            new BlockhashStore(_api.BlockTree, _api.SpecProvider, _api.WorldState),
            _api.LogManager,
            _api.SpecHelper,
            contractRewriter,
            new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor()));
    }

    protected override IUnclesValidator CreateUnclesValidator() => Always.Valid;

    protected override IHealthHintService CreateHealthHintService() =>
        new ManualHealthHintService(_blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint);

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;
}
