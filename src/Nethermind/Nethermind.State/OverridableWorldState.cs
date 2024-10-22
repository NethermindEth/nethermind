// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverridableWorldState : WorldState
{
    private readonly OverlayTrieStore _trieStore;
    private readonly IReadOnlyDb _codeDb;

    public OverridableWorldState(
        OverlayTrieStore trieStore, IReadOnlyDb codeDb, ILogManager? logManager,
        PreBlockCaches? preBlockCaches = null, bool populatePreBlockCache = true
    ) : base(trieStore, codeDb, logManager, preBlockCaches, populatePreBlockCache)
    {
        _trieStore = trieStore;
        _codeDb = codeDb;
    }

    /// <summary>
    /// Resets changes applied via <see cref="Nethermind.Evm.StateOverridesExtensions.ApplyStateOverrides"/>
    /// </summary>
    public void ResetOverrides()
    {
        _trieStore.ResetOverrides();
        _codeDb.ClearTempChanges();
    }
}
