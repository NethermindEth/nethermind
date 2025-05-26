// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
using Nethermind.Consensus.Withdrawals;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.State;
using Nethermind.Taiko.BlockTransactionExecutors;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(TaikoNethermindApi api) : InitializeBlockchain(api)
{
    private readonly TaikoNethermindApi _api = api;
    private readonly IBlocksConfig _blocksConfig = api.Config<IBlocksConfig>();

    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        _api.Context.Resolve<InvalidChainTracker>().SetupBlockchainProcessorInterceptor(_api.MainProcessingContext!.BlockchainProcessor);
    }

    protected override ITransactionProcessor CreateTransactionProcessor(CodeInfoRepository codeInfoRepository, IVirtualMachine virtualMachine, IWorldState worldState)
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

        return new TaikoTransactionProcessor(
            _api.SpecProvider,
            worldState,
            virtualMachine,
            codeInfoRepository,
            _api.LogManager
        );
    }

    protected override BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer, ITransactionProcessor transactionProcessor, IWorldState worldState)
    {
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));

        return new BlockProcessor(_api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource.Get(transactionProcessor),
            new BlockInvalidTxExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), worldState),
            worldState,
            _api.ReceiptStorage!,
            new BeaconBlockRootHandler(transactionProcessor, worldState),
            new BlockhashStore(_api.SpecProvider, worldState),
            _api.LogManager,
            new WithdrawalProcessor(worldState, _api.LogManager),
            new ExecutionRequestsProcessor(transactionProcessor),
            preWarmer: preWarmer);
    }

    protected override IHealthHintService CreateHealthHintService() =>
        new ManualHealthHintService(_blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint);

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => NeverStartBlockProductionPolicy.Instance;
}
