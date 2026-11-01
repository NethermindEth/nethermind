// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

/// <summary>Builds environments whose world state reads and writes through a caller-supplied wrapper of the env's own
/// scope provider, so a background re-execution can observe its state access. Every other environment is built by
/// <see cref="IOverridableEnvFactory"/> and pays nothing for it.</summary>
public interface IScopeDecoratingEnvFactory
{
    /// <summary>Creates an env; <paramref name="decorateScopeProvider"/>, when given, wraps the scope provider before
    /// the env resolves its world state.</summary>
    IOverridableEnv Create(Func<IWorldStateScopeProvider, IWorldStateScopeProvider>? decorateScopeProvider);
}
