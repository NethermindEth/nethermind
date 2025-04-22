// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Utils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks.Config;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Init;

public class PruningTrieStateFactory(
    ISyncConfig syncConfig,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    IProcessExitSource processExit,
    DisposableStack disposeStack,
    ILogManager logManager
)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PruningTrieStateFactory>();

    public (IWorldStateManager, INodeStorage, IPruningTrieStateAdminRpcModule) Build()
    {
        if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
            syncConfig.DownloadBodiesInFastSync = true;
        }

        IKeyValueStoreWithBatching codeDb = dbProvider.CodeDb;

        INodeStorage mainNodeStorage = new NullNodeStorage();

        VerkleTreeStore<VerkleSyncCache> verkleTreeStore = new VerkleTreeStore<VerkleSyncCache>(dbProvider, logManager);
        IWorldState worldState  = new VerkleWorldState(new VerkleStateTree(verkleTreeStore, logManager), codeDb, logManager);


        // Init state if we need system calls before actual processing starts
        worldState.StateRoot = blockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;
        if (blockTree!.Head?.StateRoot is not null)
        {
            worldState.StateRoot = blockTree.Head.StateRoot;
        }

        IWorldStateManager stateManager =  new VerkleWorldStateManager(
            worldState,
            verkleTreeStore,
            dbProvider,
            logManager);

        // NOTE: Don't forget this! Very important!
        TrieStoreBoundaryWatcher trieStoreBoundaryWatcher = new(stateManager, blockTree!, logManager);
        // Must be disposed after main trie store or the final persist on dispose will not set persisted state on blocktree.
        disposeStack.Push(trieStoreBoundaryWatcher);
        var verifyTrieStarter = new VerifyTrieStarter(stateManager, processExit!, logManager);
        ManualPruningTrigger pruningTrigger = new();
        PruningTrieStateAdminRpcModule adminRpcModule = new PruningTrieStateAdminRpcModule(
            pruningTrigger,
            blockTree,
            stateManager.GlobalStateReader,
            verifyTrieStarter!
        );

        return (stateManager, mainNodeStorage, adminRpcModule);
    }
}
