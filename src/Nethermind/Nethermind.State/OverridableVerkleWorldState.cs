// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class OverridableVerkleWorldState(
    OverlayVerkleTreeStore trieStore,
    IReadOnlyDbProvider dbProvider,
    ILogManager? logManager,
    PreBlockCaches? preBlockCaches = null,
    bool populatePreBlockCache = true)
    : VerkleWorldState(trieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager, preBlockCaches, populatePreBlockCache), IOverridableWorldState
{

    /// <summary>
    /// Resets changes applied via <see cref="Nethermind.Evm.StateOverridesExtensions.ApplyStateOverrides"/>
    /// </summary>
    public void ResetOverrides()
    {
        trieStore.ResetOverrides();
        dbProvider.ClearTempChanges();
    }
}
