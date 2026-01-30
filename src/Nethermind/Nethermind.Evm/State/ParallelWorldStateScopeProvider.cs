// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.State;

/// <summary>
/// A decorator for <see cref="IWorldStateScopeProvider"/> that enables block access list tracing
/// when <see cref="ParallelWorldState"/> is used.
/// </summary>
public class ParallelWorldStateScopeProvider(IWorldStateScopeProvider innerProvider) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => innerProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => innerProvider.BeginScope(baseBlock);
}
