// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class ScopedReadOnlyWorldStateManager: IScopedWorldStateManager
{
    public PreBlockCaches? Caches { get; }
    public IWorldState GlobalWorldState { get; }
    public IStateReader GlobalStateReader { get; }
    public IReadOnlyTrieStore TrieStore { get; }
    public IWorldState GetGlobalWorldState(BlockHeader blockHeader) => GlobalWorldState;

    public ScopedReadOnlyWorldStateManager(IWorldState readOnlyWorldState,
        IDbProvider dbProvider,
        ITrieStore readOnlyTrieStore,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null)
    {
        GlobalWorldState = readOnlyWorldState;
        TrieStore = readOnlyTrieStore.AsReadOnly();

        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        ReadOnlyDb codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new StateReader(TrieStore, codeDb, logManager);
        Caches = preBlockCaches;
    }
    public bool ClearCache() => Caches?.Clear() == true;
    public bool HasStateRoot(Hash256 root) => GlobalStateReader.HasStateForRoot(root);

    public IScopedWorldStateManager CreateResettableWorldStateManager()
    {
        throw new NotImplementedException("cannot get scope from scoped world state manager");
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }
}
