// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

public static class WorldStateScopeExtensions
{
    public static T WithScope<T>(this IWorldState worldState, Func<IWorldState, T> func)
    {
        using var _ = worldState.BeginScope();
        return func(worldState);
    }

    public static T WithScope<T>(this IWorldState worldState, Hash256 stateRoot, Func<IWorldState, T> func)
    {
        using var _ = worldState.BeginScope(stateRoot);
        return func(worldState);
    }
}
