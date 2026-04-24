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
        {
            if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null)
            {
                return;
            }

            Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
            SystemCall systemTx = new(eip2935Account, blockHeader.ParentHash.Bytes.ToArray());

            balManager.GetTxProcessor(0).Execute(systemTx, NullTxTracer.Instance);
        }

        public override void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
            => balManager.ProcessExecutionRequests(block, receipts, spec);

        public override void ProcessWithdrawals(Block block, IReleaseSpec spec)
            => balManager.ProcessWithdrawals(block, spec);
    }
}
