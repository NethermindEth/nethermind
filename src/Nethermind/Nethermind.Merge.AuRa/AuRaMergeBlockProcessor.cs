// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Requests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    ILogManager logManager,
    IBlockTree blockTree,
    IWithdrawalProcessor withdrawalProcessor,
    ITransactionProcessor transactionProcessor,
    IAuRaValidator? validator,
    ITxFilter? txFilter = null,
    AuRaContractGasLimitOverride? gasLimitOverride = null,
    ContractRewriter? contractRewriter = null,
    IBlockCachePreWarmer? preWarmer = null,
    IConsensusRequestsProcessor? consensusRequestsProcessor = null)
    : AuRaBlockProcessor(specProvider,
        blockValidator,
        rewardCalculator,
        blockTransactionsExecutor,
        stateProvider,
        receiptStorage,
        beaconBlockRootHandler,
        logManager,
        blockTree,
        withdrawalProcessor,
        transactionProcessor,
        validator,
        txFilter,
        gasLimitOverride,
        contractRewriter,
        preWarmer,
        consensusRequestsProcessor)
{
    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options) =>
        block.IsPostMerge
            ? PostMergeProcessBlock(block, blockTracer, options)
            : base.ProcessBlock(block, blockTracer, options);
}
