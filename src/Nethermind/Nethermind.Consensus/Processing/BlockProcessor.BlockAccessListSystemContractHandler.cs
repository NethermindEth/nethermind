// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public sealed class BlockAccessListSystemContractHandler(IBlockAccessListManager balManager) : ISystemContractHandler
    {
        public void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer)
            => balManager.StoreBeaconRoot(block, spec);

        public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
            => balManager.ApplyBlockhashStateChanges(blockHeader, spec);

        public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
            => balManager.ProcessExecutionRequests(block, receipts, spec);

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
            => balManager.ProcessWithdrawals(block, spec);

        public void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer)
            => balManager.CommitIndexTableRoots(block, receipts, spec, tracer);

        public void RollbackBlock(Block block)
            => balManager.RollbackBlock(block);

        public void UpdateFinalBlockHash(Block block)
            => balManager.UpdateFinalBlockHash(block);
    }
}
