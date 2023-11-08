// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class ReadOnlyWorldStateFactory: IWorldStateFactory
{
    private IReadOnlyTrieStore? _readOnlyTrieStore;
    private IKeyValueStore _codeDb;
    private ILogManager _logManager;

    public ReadOnlyWorldStateFactory(
        IReadOnlyTrieStore? readOnlyTrieStore,
        IKeyValueStore codeDb,
        ILogManager logManager
    )
    {
        _readOnlyTrieStore = readOnlyTrieStore;
        _codeDb = codeDb;
        _logManager = logManager;
    }

    public IWorldState CreateWorldState()
    {
        return new WorldState(_readOnlyTrieStore, _codeDb, _logManager);
    }

    public IStateReader CreateStateReader()
    {
        return new StateReader(_readOnlyTrieStore, _codeDb, _logManager);
    }
}
