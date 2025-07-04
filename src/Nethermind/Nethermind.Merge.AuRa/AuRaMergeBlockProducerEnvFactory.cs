// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProducerEnvFactory(
    ChainSpec chainSpec,
    IAbiEncoder abiEncoder,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculatorSource rewardCalculatorSource,
    ILifetimeScope lifetimeScope,
    IWorldStateManager worldStateManager,
    IBlockProducerTxSourceFactory blockProducerTxSourceFactory,
    ILogManager logManager)
    : BlockProducerEnvFactory(lifetimeScope, worldStateManager, blockProducerTxSourceFactory)
{
    private readonly IReceiptStorage _receiptStorage = NullReceiptStorage.Instance;

    protected override ContainerBuilder ConfigureBuilder(ContainerBuilder builder) =>
        base.ConfigureBuilder(builder)
            .AddScoped(CreateBlockProcessor);

    private IBlockProcessor CreateBlockProcessor(
        ITransactionProcessor txProcessor,
        IWorldState worldState,
        IBlockProcessor.IBlockTransactionsExecutor txExecutor,
        IExecutionRequestsProcessor executionRequestsProcessor
    )
    {
        var withdrawalContractFactory = new WithdrawalContractFactory(
            chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>(), abiEncoder);

        return new AuRaMergeBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculatorSource.Get(txProcessor),
            txExecutor,
            worldState,
            _receiptStorage,
            new BeaconBlockRootHandler(txProcessor, worldState),
            logManager,
            blockTree,
            new Consensus.Withdrawals.BlockProductionWithdrawalProcessor(
                new AuraWithdrawalProcessor(
                    withdrawalContractFactory.Create(txProcessor),
                    logManager
                )
            ),
            executionRequestsProcessor,
            null);
    }
}
