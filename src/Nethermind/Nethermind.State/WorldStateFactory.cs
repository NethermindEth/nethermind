// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateFactory: IWorldStateFactory
{
    private ITrieStore _trieStore;
    private IKeyValueStore _codeDb;
    private ILogManager _logManager;

    public WorldStateFactory(
        TrieStore trieStore,
        IKeyValueStore codeDb,
        ILogManager logManager
    )
    {
        _trieStore = trieStore;
        _codeDb = codeDb;
        _logManager = logManager;
    }

    public IWorldState CreateWorldState()
    {
        return new WorldState(_trieStore, _codeDb, _logManager);
    }

    public IStateReader CreateStateReader()
    {
        return new StateReader(_trieStore, _codeDb, _logManager);
    }

    public (IWorldState, IStateReader, Action) CreateResettableWorldState()
    {
        throw new InvalidOperationException("Unsupported action");
    }
}
