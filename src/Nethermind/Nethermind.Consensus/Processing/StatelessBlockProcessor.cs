// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Verkle.Curve;

namespace Nethermind.Consensus.Processing;

public class StatelessBlockProcessor : BlockProcessor, IBlockProcessor
{
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;

    bool IBlockProcessor.CanProcessStatelessBlock => true;

    public StatelessBlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldState? stateProvider,
        IReceiptStorage? receiptStorage,
        IWitnessCollector? witnessCollector,
        IBlockTree? blockTree,
        ILogManager? logManager,
        IWithdrawalProcessor? withdrawalProcessor = null)
        : base(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            witnessCollector,
            blockTree,
            logManager,
            withdrawalProcessor)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager)); ;
        _logger = _logManager.GetClassLogger<StatelessBlockProcessor>();
    }

    protected override void InitBranch(Hash256 branchStateRoot, bool incrementReorgMetric = true)
    {

    }

    protected override (IBlockProcessor.IBlockTransactionsExecutor, IWorldState) GetOrCreateExecutorAndState(Block block)
    {
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor;
        IWorldState worldState;
        if (!block.IsGenesis)
        {
            block.Header.MaybeParent!.TryGetTarget(out BlockHeader maybeParent);
            Banderwagon stateRoot = Banderwagon.FromBytes(maybeParent!.StateRoot!.Bytes.ToArray())!.Value;
            worldState = new VerkleWorldState(block.ExecutionWitness!, stateRoot, _logManager);
            blockTransactionsExecutor = _blockTransactionsExecutor.WithNewStateProvider(worldState);
        }
        else
        {
            blockTransactionsExecutor = _blockTransactionsExecutor;
            worldState = _stateProvider;
        }

        return (blockTransactionsExecutor, worldState);
    }
}
