// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
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
    private readonly IPoSSwitcher _poSSwitcher;

    public AuRaMergeBlockProcessor(
        IPoSSwitcher poSSwitcher,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IStateProvider stateProvider,
        IStorageProvider storageProvider,
        IReceiptStorage receiptStorage,
        ILogManager logManager,
        IBlockTree blockTree,
        IWithdrawalProcessor withdrawalProcessor,
        ITxFilter? txFilter = null,
        AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null
    ) : base(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            storageProvider,
            receiptStorage,
            logManager,
            blockTree,
            withdrawalProcessor,
            txFilter,
            gasLimitOverride,
            contractRewriter
        )
    {
        _poSSwitcher = poSSwitcher;
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options) =>
        _poSSwitcher.IsPostMerge(block.Header)
            ? PostMergeProcessBlock(block, blockTracer, options)
            : base.ProcessBlock(block, blockTracer, options);
}
