// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.AuRa.Config;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProcessor(
    ISpecProvider specProvider,
    AuRaChainSpecEngineParameters chainSpecEngineParameters,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    ILogManager logManager,
    IBlockFinder blockTree,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockAccessListManager balManager,
    IAuRaValidator? validator,
    ITxFilter? txFilter = null,
    AuRaContractGasLimitOverride? gasLimitOverride = null,
    ContractRewriter? contractRewriter = null)
    : AuRaBlockProcessor(specProvider,
        chainSpecEngineParameters,
        blockValidator,
        rewardCalculator,
        blockTransactionsExecutor,
        stateProvider,
        receiptStorage,
        beaconBlockRootHandler,
        logManager,
        blockTree,
        withdrawalProcessor,
        executionRequestsProcessor,
        balManager,
        validator,
        txFilter,
        gasLimitOverride,
        contractRewriter)
{
    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, IReleaseSpec spec, CancellationToken token) =>
        block.IsPostMerge
            ? PostMergeProcessBlock(block, blockTracer, options, spec, token)
            : base.ProcessBlock(block, blockTracer, options, spec, token);
}
