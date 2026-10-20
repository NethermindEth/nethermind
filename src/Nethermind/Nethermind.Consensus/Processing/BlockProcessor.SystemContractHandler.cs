// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.IndexTables;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public interface ISystemContractHandler
        : IIndexTableHandler
    {
        void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer);
        void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec);
        void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec);
        void ProcessWithdrawals(Block block, IReleaseSpec spec);
    }

    public sealed class SystemContractHandler(
        IBeaconBlockRootHandler beaconBlockRootHandler,
        IBlockhashStore blockHashStore,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IIndexTableHandler indexTableHandler) : ISystemContractHandler
    {
        public void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer)
            => beaconBlockRootHandler.StoreBeaconRoot(block, spec, tracer);

        public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
            => blockHashStore.ApplyBlockhashStateChanges(blockHeader, spec);

        public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
            => executionRequestsProcessor.ProcessExecutionRequests(block, state, receipts, spec);

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
            => withdrawalProcessor.ProcessWithdrawals(block, spec);

        /// <inheritdoc />
        public void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer)
            => indexTableHandler.CommitIndexTableRoots(block, receipts, spec, tracer);

        /// <inheritdoc />
        public void RollbackBlock(Block block)
            => indexTableHandler.RollbackBlock(block);

        /// <inheritdoc />
        public void UpdateFinalBlockHash(Block block)
            => indexTableHandler.UpdateFinalBlockHash(block);
    }
}
