// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

public partial class BlockAccessListManager
{
    public IReadOnlyStateProvider? GetPostTransactionsState(Block block)
    {
        if (!ParallelExecutionEnabled || block.BlockAccessList is not { } blockAccessList) return null;

        CheckInitialized();
        IReadOnlyStateProvider postExecutionIndexState = _txProcessorWithWorldStateManager.GetPostExecution().BlockAccessListWorldState!;
        return new PostTransactionsStateReader(stateProvider, postExecutionIndexState, blockAccessList);
    }

    /// <summary>
    /// The block's state after its transactions, in parallel execution where the world state already holds the whole
    /// block access list: an account the list declares is read from the list at the post-execution index, any other
    /// account from the world state, which the block never changed.
    /// </summary>
    private sealed class PostTransactionsStateReader(
        IReadOnlyStateProvider blockState,
        IReadOnlyStateProvider postExecutionIndexState,
        ReadOnlyBlockAccessList blockAccessList) : IReadOnlyStateProvider
    {
        public Hash256 StateRoot => throw new NotSupportedException("The state root after the transactions is not computed.");

        public bool TryGetAccount(Address address, out AccountStruct account) => StateOf(address).TryGetAccount(address, out account);

        public ReadOnlyMemory<byte> GetCode(Address address) => StateOf(address).GetCode(address);

        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => blockState.GetCode(in codeHash);

        public bool IsContract(Address address) => StateOf(address).IsContract(address);

        public bool AccountExists(Address address) => StateOf(address).AccountExists(address);

        public bool IsDeadAccount(Address address) => StateOf(address).IsDeadAccount(address);

        public void Get(in StorageCell storageCell, out UInt256 value) => StateOf(storageCell.Address).Get(in storageCell, out value);

        private IReadOnlyStateProvider StateOf(Address address) =>
            blockAccessList.GetAccountChanges(address) is null ? blockState : postExecutionIndexState;
    }
}
