// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

public interface IOverridableEnvFactory
{
    IOverridableEnv Create();

    /// <summary>Creates an env whose world state uses the scope provider <paramref name="decorateScopeProvider"/>
    /// returns for the env's own; the decorator is applied before the env resolves its world state.</summary>
    IOverridableEnv Create(Func<IWorldStateScopeProvider, IWorldStateScopeProvider> decorateScopeProvider);
}
