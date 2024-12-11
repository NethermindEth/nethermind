// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State;

public class OverridableWorldState(
    IStateFactory factory,
    IReadOnlyDbProvider dbProvider,
    ILogManager? logManager,
    PreBlockCaches? preBlockCaches = null)
    : WorldState(factory, dbProvider.GetDb<IDb>(DbNames.Code), logManager, preBlockCaches)
{
    /// <summary>
    /// Resets changes applied via <see cref="Nethermind.Evm.StateOverridesExtensions.ApplyStateOverrides"/>
    /// </summary>
    public void ResetStateAndOverrides()
    {
        // Fully reset the world state
        FullReset();

        // Clear any changes that were applied on the database.
        dbProvider.ClearTempChanges();
    }
}
