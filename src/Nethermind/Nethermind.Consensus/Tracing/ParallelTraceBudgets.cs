// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Tracing;

/// <summary>The worker budget for each kind of seed a whole-block trace can stand on: a block that carries an access
/// list is seeded from it, any other block from the flat history changesets. Each has its own setting, and a block is
/// traced in parallel only when the budget of the seed it takes allows two workers or more.</summary>
/// <remarks>Borrows <paramref name="changesets"/>, null when no changeset seed source exists, and owns
/// <paramref name="accessLists"/>.</remarks>
public sealed class ParallelTraceBudgets(ISpecProvider specProvider, ParallelTraceBudget? changesets, ParallelTraceBudget accessLists) : IDisposable
{
    private readonly bool _chainHasAccessLists = specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;

    /// <summary>Whether any block this chain carries could be traced by two workers or more.</summary>
    public bool AllowsParallelTracing => (_chainHasAccessLists && accessLists.Degree >= 2) || changesets?.Degree >= 2;

    /// <summary>The most workers any one block may use.</summary>
    public int MaxDegree => Math.Max(_chainHasAccessLists ? accessLists.Degree : 1, changesets?.Degree ?? 1);

    /// <summary>The budget of the seed <paramref name="header"/> takes; false when it allows a single worker only.</summary>
    public bool TryGetParallel(BlockHeader header, [NotNullWhen(true)] out ParallelTraceBudget? budget)
    {
        budget = _chainHasAccessLists && specProvider.GetSpec(header).BlockLevelAccessListsEnabled ? accessLists : changesets;
        return budget?.Degree >= 2;
    }

    public void Dispose() => accessLists.Dispose();
}
