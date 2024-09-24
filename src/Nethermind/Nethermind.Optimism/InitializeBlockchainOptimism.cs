// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism(OptimismNethermindApi api) : InitializeBlockchain(api)
{
    private readonly IBlocksConfig _blocksConfig = api.Config<IBlocksConfig>();

    protected override Task InitBlockchain()
    {
        api.RegisterTxType<OptimismTransactionForRpc>(new OptimismTxDecoder<Transaction>(), Always.Valid);

        api.SpecHelper = new(api.ChainSpec.Optimism);
        api.L1CostHelper = new(api.SpecHelper, api.ChainSpec.Optimism.L1BlockAddress);

        return base.InitBlockchain();
    }

    protected override ITransactionProcessor CreateTransactionProcessor(CodeInfoRepository codeInfoRepository, VirtualMachine virtualMachine)
    {
        if (api.SpecProvider is null) throw new StepDependencyException(nameof(api.SpecProvider));
        if (api.SpecHelper is null) throw new StepDependencyException(nameof(api.SpecHelper));
        if (api.L1CostHelper is null) throw new StepDependencyException(nameof(api.L1CostHelper));
        if (api.WorldState is null) throw new StepDependencyException(nameof(api.WorldState));

        return new OptimismTransactionProcessor(
            api.SpecProvider,
            api.WorldState,
            virtualMachine,
            api.LogManager,
            api.L1CostHelper,
            api.SpecHelper,
            codeInfoRepository
        );
    }

    protected override IHeaderValidator CreateHeaderValidator()
    {
        if (api.InvalidChainTracker is null) throw new StepDependencyException(nameof(api.InvalidChainTracker));

        OptimismHeaderValidator opHeaderValidator = new(
            api.BlockTree,
            api.SealValidator,
            api.SpecProvider,
            api.LogManager);

        return new InvalidHeaderInterceptor(opHeaderValidator, api.InvalidChainTracker, api.LogManager);
    }

    protected override IBlockValidator CreateBlockValidator()
    {
        if (api.InvalidChainTracker is null) throw new StepDependencyException(nameof(api.InvalidChainTracker));
        return new InvalidBlockInterceptor(base.CreateBlockValidator(), api.InvalidChainTracker, api.LogManager);
    }

    protected override BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer)
    {
        ITransactionProcessor? transactionProcessor = api.TransactionProcessor;
        if (api.DbProvider is null) throw new StepDependencyException(nameof(api.DbProvider));
        if (api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(api.RewardCalculatorSource));
        if (transactionProcessor is null) throw new StepDependencyException(nameof(transactionProcessor));
        if (api.SpecHelper is null) throw new StepDependencyException(nameof(api.SpecHelper));
        if (api.SpecProvider is null) throw new StepDependencyException(nameof(api.SpecProvider));
        if (api.BlockTree is null) throw new StepDependencyException(nameof(api.BlockTree));
        if (api.WorldState is null) throw new StepDependencyException(nameof(api.WorldState));

        Create2DeployerContractRewriter contractRewriter = new(api.SpecHelper, api.SpecProvider, api.BlockTree);

        return new OptimismBlockProcessor(
            api.SpecProvider,
            api.BlockValidator,
            api.RewardCalculatorSource.Get(transactionProcessor),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, api.WorldState),
            api.WorldState,
            api.ReceiptStorage,
            transactionProcessor,
            new BlockhashStore(api.SpecProvider, api.WorldState),
            new BeaconBlockRootHandler(transactionProcessor),
            api.LogManager,
            api.SpecHelper,
            contractRewriter,
            new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor()),
            preWarmer: preWarmer);
    }

    protected override IUnclesValidator CreateUnclesValidator() => Always.Valid;

    protected override IHealthHintService CreateHealthHintService() =>
        new ManualHealthHintService(_blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint);

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;
}
