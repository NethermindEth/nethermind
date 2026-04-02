// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Crypto;
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
    public new event Action? TransactionsExecuted;
    private BlockAccessListManager _balManager;
    private bool _parallel;

    public override (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (spec.BlockLevelAccessListsEnabled && !suggestedBlock.IsGenesis)
        {
            _parallel = blocksConfig.ParallelExecution && !options.ContainsFlag(ProcessingOptions.ProducingBlock);
            _balManager = new(_stateProvider, blobBaseFeeCalculator, _specProvider, blockHashProvider, _logManager, blocksConfig);
            _blockTransactionsExecutor.SetBlockAccessListManager(_balManager);
            _balManager.SetGasUsed(suggestedBlock.GasUsed);

            if (_parallel)
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
        if (!spec.BlockLevelAccessListsEnabled || block.IsGenesis)
        {
            return base.ProcessBlock(block, blockTracer, options, spec, token);
        }

        BlockBody body = block.Body;
        BlockHeader header = block.Header;

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        _blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header, spec));

        _balManager.Setup(block, _parallel);

        _balManager.StoreBeaconRoot(block, spec);
        _balManager.ApplyBlockhashStateChanges(header, spec);
        _stateProvider.Commit(spec, commitRoots: false);

        TxReceipt[] receipts;
        receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)
        // to free the thread pool for blooms, receipts root, state root parallel work below
        TransactionsExecuted?.Invoke();

        _stateProvider.Commit(spec, commitRoots: false);

        CalculateBlooms(receipts);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);

        _balManager.ProcessWithdrawals(block, spec, options);

        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // the spec has Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.
        _stateProvider.Commit(spec, commitRoots: false);

        _balManager.ProcessExecutionRequests(block, receipts, spec);

        ReceiptsTracer.EndBlockTrace();

        _stateProvider.Commit(spec, commitRoots: true);

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            SetAccountChanges(block);
        }

        if (ShouldComputeStateRoot(header))
        {
            _stateProvider.RecalculateStateRoot();
            header.StateRoot = _stateProvider.StateRoot;
        }

        _balManager.SetBlockAccessList(block, spec);

        header.Hash = header.CalculateHash();

        return receipts;
    }
}
