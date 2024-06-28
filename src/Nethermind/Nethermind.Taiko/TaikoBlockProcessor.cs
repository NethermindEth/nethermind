// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoBlockProcessor : BlockProcessor
{
    public TaikoBlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldState? stateProvider,
        IReceiptStorage? receiptStorage,
        IBlockhashStore? blockhashStore,
        ILogManager? logManager,
        ITaikoSpecHelper opSpecHelper,
        IWithdrawalProcessor? withdrawalProcessor = null,
        IBlockCachePreWarmer? preWarmer = null)
        : base(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor,
            stateProvider, receiptStorage, blockhashStore, logManager, withdrawalProcessor,
            ReceiptsRootCalculator.Instance, preWarmer)
    {
        ArgumentNullException.ThrowIfNull(stateProvider);
        // ReceiptsTracer = new TaikoReceiptsTracer(opSpecHelper, stateProvider);
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
    {
        block.Transactions[0].IsAnchorTx = true;
        return base.ProcessBlock(block, blockTracer, options);
    }
}
