// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.State;

public class OverridableWorldScope(
    IReadOnlyDbProvider readOnlyDbProvider,
    IWorldState worldState,
    IStateReader stateReader)
    : IOverridableWorldScope
{
    public IWorldState WorldState { get; } = worldState;
    public IStateReader GlobalStateReader => stateReader;
    public void ResetOverrides()
    {
        readOnlyDbProvider.ClearTempChanges();
    }
}
