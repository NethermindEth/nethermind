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

public class ReadOnlyTxProcessingEnvBase : IReadOnlyTxProcessingEnvBase
{
    public IStateReader StateReader { get; }
    public IWorldState StateProvider { get; }
    public IBlockTree BlockTree { get; }
    public IReadOnlyDbProvider DbProvider { get; }
    public IBlockhashProvider BlockhashProvider { get; }

    public ReadOnlyTxProcessingEnvBase(
        IReadOnlyDbProvider? readOnlyDbProvider,
        ITrieStore? trieStore,
        IBlockTree? blockTree,
        ILogManager? logManager
    )
    {
        DbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
        ReadOnlyDb codeDb = readOnlyDbProvider.CodeDb.AsReadOnly(true);

        StateReader = new StateReader(trieStore, codeDb, logManager);
        StateProvider = new WorldState(trieStore, codeDb, logManager);

        BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        BlockhashProvider = new BlockhashProvider(BlockTree, logManager);
            
    }
}
