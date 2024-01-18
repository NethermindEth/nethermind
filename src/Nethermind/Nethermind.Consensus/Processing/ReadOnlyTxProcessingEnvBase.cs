// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvBase
{
    public IStateReader StateReader { get; }
    public IWorldState StateProvider { get; }
    public IBlockTree BlockTree { get; }
    public IReadOnlyDbProvider DbProvider { get; }
    public IBlockhashProvider BlockhashProvider { get; }

    protected ReadOnlyTxProcessingEnvBase(
        IWorldStateManager worldStateManager,
        IBlockTree readOnlyBlockTree,
        ILogManager? logManager
    )
    {
        StateReader = worldStateManager.GlobalStateReader;
        StateProvider = worldStateManager.CreateResettableWorldState();
        BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
        BlockhashProvider = new BlockhashProvider(BlockTree, logManager);
    }
}
