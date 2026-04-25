// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public sealed class BlockAccessListSystemContractHandler(
        IBeaconBlockRootHandler beaconBlockRootHandler,
        IBlockhashStore blockHashStore,
        IBlockAccessListManager balManager)
        : ISystemContractHandler
    {
        public (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec, bool includeStorageCells = true)
            => beaconBlockRootHandler.BeaconRootsAccessList(block, spec, includeStorageCells);

        public void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer)
            => balManager.StoreBeaconRoot(block, spec);

        public AccessList? GetAccessList(Block block, IReleaseSpec spec)
            => beaconBlockRootHandler.GetAccessList(block, spec);

        public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
        {
            if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null)
            {
                return;
            }

            Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
            using ArrayPoolDisposableReturn _ = ArrayPoolDisposableReturn.Rent(Hash256.Size, out byte[] parentHashData);
            blockHeader.ParentHash.Bytes.CopyTo(parentHashData);
            SystemCall systemTx = new(eip2935Account)
            {
                Data = new System.ReadOnlyMemory<byte>(parentHashData, 0, Hash256.Size),
            };

            balManager.GetTxProcessor(0).Execute(systemTx, NullTxTracer.Instance);
        }

        public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, long requiredBlockNumber, IReleaseSpec spec)
            => blockHashStore.GetBlockHashFromState(currentBlockHeader, requiredBlockNumber, spec);

        public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
            => balManager.ProcessExecutionRequests(block, receipts, spec);

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
            => balManager.ProcessWithdrawals(block, spec);
    }
}
