// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverridableWorldState(
    OverlayTrieStore trieStore,
    IReadOnlyDbProvider dbProvider,
    ILogManager? logManager,
    PreBlockCaches? preBlockCaches = null,
    bool populatePreBlockCache = true)
    : WorldState(trieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager, preBlockCaches, populatePreBlockCache), IOverridableWorldState
{

    /// <summary>
    /// Resets changes applied via <see cref="Nethermind.Evm.StateOverridesExtensions.ApplyStateOverrides"/>
    /// </summary>
    public void ResetOverrides()
    {
        dbProvider.ClearTempChanges();
    }
}
