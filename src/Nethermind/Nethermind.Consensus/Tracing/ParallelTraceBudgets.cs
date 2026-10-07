// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Tracing;

/// <summary>Which blocks a whole-block trace may run in parallel, and the one node-wide budget every such trace draws
/// on. A block that carries an access list is seeded from it, any other block from the flat history changesets when
/// <paramref name="changesetSeeds"/> is set. Both kinds share the budget, so its degree caps the tracing workers of the
/// whole node, and a block is traced in parallel only when the budget allows two workers or more.</summary>
/// <remarks>Borrows <paramref name="budget"/>.</remarks>
public sealed class ParallelTraceBudgets(ISpecProvider specProvider, ParallelTraceBudget budget, bool changesetSeeds)
{
    private readonly bool _chainHasAccessLists = specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;

    /// <summary>Whether any block this chain carries could be traced by two workers or more.</summary>
    public bool AllowsParallelTracing => (_chainHasAccessLists || changesetSeeds) && budget.Degree >= 2;

    /// <summary>The most workers every parallel trace on this node may hold at once.</summary>
    public int Degree => budget.Degree;

    /// <summary>The shared budget when <paramref name="header"/> takes a seed; false when it takes none or the budget
    /// allows a single worker only.</summary>
    public bool TryGetParallel(BlockHeader header, [NotNullWhen(true)] out ParallelTraceBudget? slots)
    {
        bool seeded = changesetSeeds || (_chainHasAccessLists && specProvider.GetSpec(header).BlockLevelAccessListsEnabled);
        slots = seeded && budget.Degree >= 2 ? budget : null;
        return slots is not null;
    }
}
