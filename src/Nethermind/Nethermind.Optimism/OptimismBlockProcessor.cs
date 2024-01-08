// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private Create2DeployerContractRewriter? _contractRewriter;

    public OptimismBlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldState? stateProvider,
        IReceiptStorage? receiptStorage,
        IWitnessCollector? witnessCollector,
        ILogManager? logManager,
        IOPConfigHelper opConfigHelper,
        Create2DeployerContractRewriter contractRewriter,
        IWithdrawalProcessor? withdrawalProcessor = null)
        : base(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor,
            stateProvider, receiptStorage, witnessCollector, logManager, withdrawalProcessor, OptimismReceiptsRootCalculator.Instance)
    {
        ArgumentNullException.ThrowIfNull(stateProvider);
        _contractRewriter = contractRewriter;
        ReceiptsTracer = new OptimismBlockReceiptTracer(opConfigHelper, stateProvider);
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
    {
        _contractRewriter?.RewriteContract(block.Header, _stateProvider);
        return base.ProcessBlock(block, blockTracer, options);
    }
}
