// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.Taiko.ZkGas;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko-specific block processor that wraps the block tracer with a
/// <see cref="ZkGasBlockTracer"/> to meter ZK gas per opcode and precompile.
/// When Unzen is active, stores the accumulated ZK gas in <see cref="BlockHeader.Difficulty"/>
/// and recalculates the block hash. Rejects blocks (during validation) whose execution
/// would exceed the ZK gas block limit.
/// </summary>
public class TaikoBlockProcessor(
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
    ZkGasMeterHolder? zkGasMeterHolder = null)
    : BlockProcessor(
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
    private readonly ZkGasMeterHolder? _zkGasMeterHolder = zkGasMeterHolder;

    /// <summary>
    /// Wraps the incoming block tracer with ZK gas metering, delegates to
    /// <see cref="BlockProcessor.ProcessBlock"/>, then conditionally writes the
    /// total ZK gas consumed into <see cref="BlockHeader.Difficulty"/> and
    /// recalculates the block hash when Unzen is active.
    /// </summary>
    protected override TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        IReleaseSpec spec,
        CancellationToken token)
    {
        ITaikoReleaseSpec? taikoSpec = spec as ITaikoReleaseSpec;
        ZkGasBlockTracer zkGasTracer = new(
            blockTracer,
            _zkGasMeterHolder,
            taikoSpec?.UnzenBlockZkGasLimit ?? ZkGasSchedule.BlockZkGasLimit,
            taikoSpec?.UnzenTxIntrinsicZkGas ?? ZkGasSchedule.TxIntrinsicZkGas,
            taikoSpec?.UnzenOpcodeZkGasMultipliers ?? default,
            taikoSpec?.UnzenPrecompileZkGasMultipliers);

        TxReceipt[] receipts = base.ProcessBlock(block, zkGasTracer, options, spec, token);

        if (taikoSpec is { IsUnzenEnabled: true })
        {
            // During validation (NewPayload), reject blocks that exceed the ZK gas limit.
            // During production (ProducingBlock), the executor already stopped adding
            // transactions at the limit, so IsLimitExceeded is expected and not an error.
            if (zkGasTracer.Meter.IsLimitExceeded && !options.HasFlag(ProcessingOptions.ProducingBlock))
            {
                throw new InvalidOperationException("zk gas limit exceeded");
            }

            block.Header.Difficulty = (UInt256)zkGasTracer.Meter.BlockZkGasUsed;
            block.Header.Hash = block.Header.CalculateHash();
        }

        return receipts;
    }
}
