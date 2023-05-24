// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

internal class MultiCallReadOnlyTxProcessingEnv : ReadOnlyTxProcessingEnv
{
    public MultiCallReadOnlyTxProcessingEnv(
        MultiCallVirtualMachine virtualMachine,
        IReadOnlyDbProvider? readOnlyDbProvider,
        IReadOnlyTrieStore? readOnlyTrieStore,
        IReadOnlyBlockTree? readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager) : base(readOnlyDbProvider, readOnlyTrieStore, readOnlyBlockTree, specProvider, logManager)
    {
        Machine = new MultiCallVirtualMachine(virtualMachine);
        TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, Machine, logManager);
    }
}
