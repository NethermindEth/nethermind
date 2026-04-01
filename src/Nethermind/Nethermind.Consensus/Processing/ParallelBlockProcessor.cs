// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using static Nethermind.Consensus.Processing.IBlockProcessor;

namespace Nethermind.Consensus.Processing;

public partial class ParallelBlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashStore blockHashStore,
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor,
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    IBlockhashProvider blockHashProvider,
    IBlocksConfig blocksConfig)
    : BlockProcessor(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor, stateProvider, receiptStorage, beaconBlockRootHandler, blockHashStore, logManager, withdrawalProcessor, executionRequestsProcessor)
{
    private BlockAccessListManager _balManager;
    public override (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (spec.BlockLevelAccessListsEnabled && !suggestedBlock.IsGenesis)
        {
            _balManager = new(_stateProvider, blobBaseFeeCalculator, _specProvider, blockHashProvider, _logManager, blocksConfig);
            _blockTransactionsExecutor.SetBlockAccessListManager(_balManager);
            _balManager.SetGasUsed(suggestedBlock.GasUsed);

            if (!options.ContainsFlag(ProcessingOptions.ProducingBlock))
            {
                _balManager.LoadPreStateToSuggestedBlockAccessList(spec, suggestedBlock);
            }
        }
        return base.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
    }

    protected override TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        IReleaseSpec spec,
        CancellationToken token)
    {
        return [];
    }
}