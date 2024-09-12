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

namespace Nethermind.Optimism;

public class OptimismBlockProcessor : BlockProcessor
{
    private readonly Create2DeployerContractRewriter? _contractRewriter;

    public OptimismBlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldStateProvider? worldStateProvider,
        IReceiptStorage? receiptStorage,
        IBlockhashStore? blockhashStore,
        ILogManager? logManager,
        IOptimismSpecHelper opSpecHelper,
        Create2DeployerContractRewriter contractRewriter,
        IWithdrawalProcessor? withdrawalProcessor = null,
        IBlockCachePreWarmer? preWarmer = null)
        : base(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor,
            worldStateProvider, receiptStorage, blockhashStore, logManager, withdrawalProcessor,
            ReceiptsRootCalculator.Instance, preWarmer)
    {
        ArgumentNullException.ThrowIfNull(worldStateProvider);
        _contractRewriter = contractRewriter;
        ReceiptsTracer = new OptimismBlockReceiptTracer(opSpecHelper, worldStateProvider);
    }

    protected override TxReceipt[] ProcessBlock(IWorldState worldState, Block block, IBlockTracer blockTracer,
        ProcessingOptions options)
    {
        _contractRewriter?.RewriteContract(block.Header, worldState);
        return base.ProcessBlock(worldState, block, blockTracer, options);
    }
}
