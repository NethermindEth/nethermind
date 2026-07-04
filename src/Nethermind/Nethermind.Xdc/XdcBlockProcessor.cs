// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

internal class XdcBlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashStore blockHashStore,
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockAccessListManager balManager,
    IRewardsStore rewardsStore,
    IMintedRecordContract mintedRecordContract,
    ITransactionProcessor transactionProcessor) : BlockProcessor(
        specProvider,
        blockValidator,
        rewardCalculator,
        blockTransactionsExecutor,
        stateProvider,
        receiptStorage,
        beaconBlockRootHandler,
        blockHashStore,
        logManager,
        withdrawalProcessor,
        executionRequestsProcessor,
        balManager)
{
    private readonly IRewardsStore _rewardsStore = rewardsStore;
    private readonly IMintedRecordContract _mintedRecordContract = mintedRecordContract;
    private readonly ITransactionProcessor _transactionProcessor = transactionProcessor;

    protected override BlockExecutionContext CreateBlockExecutionContext(BlockHeader header, IReleaseSpec spec)
    {
        // Match Go's big.Int.Bytes() behavior: zero produces empty bytes, not [0x00].
        ValueHash256 prevRandao = ValueKeccak.Compute(
            header.Number != 0 ? header.Number.ToBigEndianSpanWithoutLeadingZeros(out _) : default);

        // XDC enables the BLOBBASEFEE opcode without blob transactions — ExcessBlobGas is never set. Check InstructionBlobBaseFee
        if (spec.BlobBaseFeeEnabled)
        {
            BlockHeader clone = header.Clone();
            clone.ExcessBlobGas = 0;
            return BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(clone, spec, prevRandao, UInt256.Zero);
        }

        return BlockExecutionContext.WithPrevRandao(header, spec, prevRandao);
    }

    protected override Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        XdcBlockHeader bh = suggestedBlock.Header as XdcBlockHeader;
        XdcBlockHeader headerForProcessing = bh.CreateHeaderForProcessing();

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.WithReplacedHeader(headerForProcessing);
    }

    protected override TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        IReleaseSpec spec,
        CancellationToken token)
    {
        bool persistEpochRewards = ShouldPersistEpochRewards(block, options);
        IBlockTracer tracerForProcessing = persistEpochRewards
            ? new RewardTracingBlockTracer(blockTracer)
            : blockTracer;

        TxReceipt[] receipts = base.ProcessBlock(block, tracerForProcessing, options, spec, token);

        if (persistEpochRewards && _rewardCalculator is XdcRewardCalculator xdcRewardCalculator)
        {
            XdcEpochRewardResult epochRewards = xdcRewardCalculator.CalculateEpochRewards(block);
            PersistEpochRewardSideEffects(block, epochRewards);
        }

        return receipts;
    }

    private bool ShouldPersistEpochRewards(Block block, ProcessingOptions options) =>
        ShouldComputeStateRoot(block.Header)
        && BlockchainProcessor.IsMainProcessingThread
        && !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);

    private void PersistEpochRewardSideEffects(Block block, XdcEpochRewardResult epochRewards)
    {
        BlockReward[] rewards = epochRewards.Rewards;
        if (rewards.Length == 0 || block.Header is not XdcBlockHeader xdcHeader)
        {
            return;
        }

        if (epochRewards.Spec is not null && epochRewards.Spec.IsTipUpgradeRewardEnabled)
        {
            _mintedRecordContract.UpdateAccounting(
                _transactionProcessor,
                xdcHeader,
                epochRewards.Spec,
                epochRewards.TotalMintedInEpoch,
                epochRewards.BurnedInOneEpoch);
        }

        _rewardsStore.SaveEpochRewards(xdcHeader.Number, rewards);
    }
}
