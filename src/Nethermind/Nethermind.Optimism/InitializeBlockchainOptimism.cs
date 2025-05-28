// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Optimism.Rpc;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism(OptimismNethermindApi api) : InitializeBlockchain(api)
{
    private readonly IBlocksConfig _blocksConfig = api.Config<IBlocksConfig>();

    protected override async Task InitBlockchain()
    {
        api.SimulateTransactionProcessorFactory = new SimulateOptimismTransactionProcessorFactory(api.L1CostHelper, api.SpecHelper);

        await base.InitBlockchain();

        api.RegisterTxType<DepositTransactionForRpc>(new OptimismTxDecoder<Transaction>(), Always.Valid);
        api.RegisterTxType<LegacyTransactionForRpc>(new OptimismLegacyTxDecoder(), new OptimismLegacyTxValidator(api.SpecProvider!.ChainId));

        api.Context.Resolve<InvalidChainTracker>().SetupBlockchainProcessorInterceptor(api.MainProcessingContext!.BlockchainProcessor);
    }

    protected override ITransactionProcessor CreateTransactionProcessor(CodeInfoRepository codeInfoRepository, IVirtualMachine virtualMachine, IWorldState worldState)
    {
        if (api.SpecProvider is null) throw new StepDependencyException(nameof(api.SpecProvider));
        if (api.SpecHelper is null) throw new StepDependencyException(nameof(api.SpecHelper));
        if (api.L1CostHelper is null) throw new StepDependencyException(nameof(api.L1CostHelper));

        return new OptimismTransactionProcessor(
            api.SpecProvider,
            worldState,
            virtualMachine,
            api.LogManager,
            api.L1CostHelper,
            api.SpecHelper,
            codeInfoRepository
        );
    }

    protected override BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer, ITransactionProcessor transactionProcessor, IWorldState worldState)
    {
        if (api.DbProvider is null) throw new StepDependencyException(nameof(api.DbProvider));
        if (api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(api.RewardCalculatorSource));
        if (api.SpecHelper is null) throw new StepDependencyException(nameof(api.SpecHelper));
        if (api.SpecProvider is null) throw new StepDependencyException(nameof(api.SpecProvider));
        if (api.BlockTree is null) throw new StepDependencyException(nameof(api.BlockTree));

        Create2DeployerContractRewriter contractRewriter = new(api.SpecHelper, api.SpecProvider, api.BlockTree);

        return new OptimismBlockProcessor(
            api.SpecProvider,
            api.BlockValidator,
            api.RewardCalculatorSource.Get(transactionProcessor),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, worldState),
            worldState,
            api.ReceiptStorage!,
            new BlockhashStore(api.SpecProvider, worldState),
            new BeaconBlockRootHandler(transactionProcessor, worldState),
            api.LogManager,
            api.SpecHelper,
            contractRewriter,
            new OptimismWithdrawalProcessor(api.WorldStateManager!.GlobalWorldState, api.LogManager, api.SpecHelper),
            new ExecutionRequestsProcessor(transactionProcessor),
            preWarmer: preWarmer);
    }

    protected override IHealthHintService CreateHealthHintService() =>
        new ManualHealthHintService(_blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint);

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;

    protected override ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider) =>
        api.Config<IOptimismConfig>().SequencerUrl is not null ? NullTxPool.Instance : base.CreateTxPool(chainHeadInfoProvider);
}
