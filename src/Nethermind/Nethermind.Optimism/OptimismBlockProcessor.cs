// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        IWorldStateManager? worldStateManager,
        IReceiptStorage? receiptStorage,
        IWitnessCollector? witnessCollector,
        IBlockTree? blockTree,
        ILogManager? logManager,
        IOPConfigHelper opConfigHelper,
        Create2DeployerContractRewriter contractRewriter,
        IWithdrawalProcessor? withdrawalProcessor = null)
        : base(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor,
            worldStateManager, receiptStorage, witnessCollector, blockTree, logManager, withdrawalProcessor, OptimismReceiptsRootCalculator.Instance)
    {
        ArgumentNullException.ThrowIfNull(worldStateManager);
        _contractRewriter = contractRewriter;
        ExecutionTracer = new OptimismBlockReceiptTracer(opConfigHelper, worldStateManager);
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
    {
        _contractRewriter?.RewriteContract(block.Header, _worldStateManager.GetGlobalWorldState(block));
        return base.ProcessBlock(block, blockTracer, options);
    }
}
