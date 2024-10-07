// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin.InvalidChainTracker;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(TaikoNethermindApi api) : InitializeBlockchain(api)
{
    private readonly TaikoNethermindApi _api = api;
    private readonly IBlocksConfig _blocksConfig = api.Config<IBlocksConfig>();

    protected override ITransactionProcessor CreateTransactionProcessor(CodeInfoRepository codeInfoRepository, VirtualMachine virtualMachine)
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));

        return new TaikoTransactionProcessor(
            _api.SpecProvider,
            _api.WorldState,
            virtualMachine,
            codeInfoRepository,
            _api.LogManager
        );
    }

    protected override IHeaderValidator CreateHeaderValidator()
    {
        if (_api.InvalidChainTracker is null) throw new StepDependencyException(nameof(_api.InvalidChainTracker));

        TaikoHeaderValidator taikoHeaderValidator = new(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);

        return new InvalidHeaderInterceptor(taikoHeaderValidator, _api.InvalidChainTracker, _api.LogManager);
    }

    protected override IBlockValidator CreateBlockValidator()
    {
        if (_api.InvalidChainTracker is null) throw new StepDependencyException(nameof(_api.InvalidChainTracker));
        if (_api.TxValidator is null) throw new StepDependencyException(nameof(_api.TxValidator));
        if (_api.HeaderValidator is null) throw new StepDependencyException(nameof(_api.HeaderValidator));
        if (_api.UnclesValidator is null) throw new StepDependencyException(nameof(_api.UnclesValidator));
        if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

        TaikoBlockValidator blockValidator = new(
            _api.TxValidator,
            _api.HeaderValidator,
            _api.UnclesValidator,
            _api.SpecProvider,
            _api.EthereumEcdsa,
            _api.LogManager);

        return new InvalidBlockInterceptor(blockValidator, _api.InvalidChainTracker, _api.LogManager);
    }

    protected override BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer)
    {
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.TransactionProcessor is null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));
        if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));

        return new BlockProcessor(
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource.Get(_api.TransactionProcessor!),
            new BlockInvalidTxExecutor(new ExecuteTransactionProcessorAdapter(_api.TransactionProcessor), _api.WorldState),
            _api.WorldState,
            _api.ReceiptStorage,
            _api.TransactionProcessor,
            new BeaconBlockRootHandler(_api.TransactionProcessor),
            new BlockhashStore(_api.SpecProvider, _api.WorldState),
            _api.LogManager,
            new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor()),
            preWarmer: preWarmer);
    }

    protected override IUnclesValidator CreateUnclesValidator() => Always.Valid;

    protected override IHealthHintService CreateHealthHintService() =>
        new ManualHealthHintService(_blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint);

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => NeverStartBlockProductionPolicy.Instance;
}
