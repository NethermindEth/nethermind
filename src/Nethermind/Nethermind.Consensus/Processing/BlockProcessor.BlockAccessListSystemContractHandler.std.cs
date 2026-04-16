// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private readonly BlockAccessListSystemContractHandler _balSystemContractHandler = new(
        beaconBlockRootHandler,
        blockHashStore,
        withdrawalProcessor,
        executionRequestsProcessor,
        balManager
    );

    public class BlockAccessListSystemContractHandler(
        IBeaconBlockRootHandler beaconBlockRootHandler,
        IBlockhashStore blockHashStore,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IBlockAccessListManager balManager)
        : SystemContractHandler(beaconBlockRootHandler, blockHashStore, withdrawalProcessor, executionRequestsProcessor)
    {
        public override void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer)
            => balManager.StoreBeaconRoot(block, spec);

        public override void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
            => balManager.ApplyBlockhashStateChanges(blockHeader, spec);

        public override void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
            => balManager.ProcessExecutionRequests(block, receipts, spec);

        public override void ProcessWithdrawals(Block block, IReleaseSpec spec)
            => balManager.ProcessWithdrawals(block, spec);
    }
}
