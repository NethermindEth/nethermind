// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class ReadOnlyVerkleWorldStateManager : IWorldStateManager
{
    private IReadOnlyDbProvider _readOnlyDbProvider;
    private IReadOnlyVerkleTreeStore? _readOnlyTrieStore;
    private ILogManager _logManager;
    private readonly IDbProvider _dbProvider;
    private readonly ReadOnlyDb _codeDb;

    public ReadOnlyVerkleWorldStateManager(
        IDbProvider dbProvider,
        IReadOnlyVerkleTreeStore? readOnlyTrieStore,
        ILogManager logManager
    )
    {
        _readOnlyTrieStore = readOnlyTrieStore;
        _dbProvider = dbProvider;
        _logManager = logManager;

        _readOnlyDbProvider = _dbProvider.AsReadOnly(false);
        _codeDb = _readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new VerkleStateReader(_readOnlyTrieStore, _codeDb, _logManager);
    }

    public virtual IWorldState GlobalWorldState => throw new InvalidOperationException("global world state not supported");

    public IStateReader GlobalStateReader { get; }

    public IWorldState CreateResettableWorldState()
    {
        return new VerkleWorldState(_readOnlyTrieStore, _codeDb, _logManager);
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }
}
