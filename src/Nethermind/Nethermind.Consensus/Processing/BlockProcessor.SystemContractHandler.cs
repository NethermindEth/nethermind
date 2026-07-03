// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public interface ISystemContractHandler
        : IBeaconBlockRootHandler, IBlockhashStore, IWithdrawalProcessor, IExecutionRequestsProcessor
    {
        /// <summary>
        /// Applies the EIP-8253 zero-nonce storage account transition (see <see cref="Eip8253Transition"/>),
        /// routing the writes through the world state whose changes are recorded at the
        /// pre-execution block access index.
        /// </summary>
        /// <returns>The number of accounts bumped.</returns>
        int ApplyEip8253Transition(IWorldState worldState, IReleaseSpec spec);
    }

    public sealed class SystemContractHandler(
        IBeaconBlockRootHandler beaconBlockRootHandler,
        IBlockhashStore blockHashStore,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor) : ISystemContractHandler
    {
        public (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec, bool includeStorageCells = true)
            => beaconBlockRootHandler.BeaconRootsAccessList(block, spec, includeStorageCells);

        public void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer)
            => beaconBlockRootHandler.StoreBeaconRoot(block, spec, tracer);

        public AccessList? GetAccessList(Block block, IReleaseSpec spec)
            => beaconBlockRootHandler.GetAccessList(block, spec);

        public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
            => blockHashStore.ApplyBlockhashStateChanges(blockHeader, spec);

        public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, ulong requiredBlockNumber, IReleaseSpec spec)
            => blockHashStore.GetBlockHashFromState(currentBlockHeader, requiredBlockNumber, spec);

        public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
            => executionRequestsProcessor.ProcessExecutionRequests(block, state, receipts, spec);

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
            => withdrawalProcessor.ProcessWithdrawals(block, spec);

        public int ApplyEip8253Transition(IWorldState worldState, IReleaseSpec spec)
            => Eip8253Transition.Apply(worldState, spec);
    }
}
