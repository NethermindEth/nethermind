// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
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
using Nethermind.State;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProcessor : AuRaBlockProcessor
{
    public AuRaMergeBlockProcessor(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        ILogManager logManager,
        IBlockTree blockTree,
        IWithdrawalProcessor withdrawalProcessor,
        IAuRaValidator? validator,
        ITxFilter? txFilter = null,
        AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null,
        IBlockCachePreWarmer? preWarmer = null
    ) : base(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            logManager,
            blockTree,
            withdrawalProcessor,
            validator,
            txFilter,
            gasLimitOverride,
            contractRewriter,
            preWarmer
        )
    { }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options) =>
        block.IsPostMerge
            ? PostMergeProcessBlock(block, blockTracer, options)
            : base.ProcessBlock(block, blockTracer, options);
}
