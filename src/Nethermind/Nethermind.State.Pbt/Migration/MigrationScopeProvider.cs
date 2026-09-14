// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Mirror;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Main-processing scope provider: flat mirrored into PBT before activation, PBT alone after.</summary>
internal sealed class MigrationScopeProvider(
    FlatWorldStateManager flat,
    PbtWorldStateManager pbt,
    IPbtDbManager pbtManager,
    IPbtResourcePool resourcePool,
    MigrationBackendSelector selector,
    ILogManager logManager) : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _flat = flat.GlobalWorldState;
    private readonly IWorldStateScopeProvider _mirror = new PbtMirrorScopeProvider(flat.GlobalWorldState, pbtManager, resourcePool, logManager);
    private readonly IWorldStateScopeProvider _pbt = pbt.GlobalWorldState;

    internal IWorldStateScopeProvider Select(BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        if (selector.IsBinary(baseBlock, targetBlock)) return _pbt;
        if (selector.PbtHas(baseBlock)) return _mirror;
        return selector.PbtAhead(baseBlock) ? _flat : _mirror;
    }

    public bool HasRoot(BlockHeader? baseBlock, BlockHeader? targetBlock) => Select(baseBlock, targetBlock).HasRoot(baseBlock, targetBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, BlockHeader? targetBlock, LocalMetrics metrics) =>
        Select(baseBlock, targetBlock).BeginScope(baseBlock, targetBlock, metrics);
}

/// <summary>Read-only composite: executes on the backend the target's spec selects, reads from whichever holds the state.</summary>
/// <remarks>
/// A read addresses a state by (number, root), and flat never holds a post-activation state, so availability alone
/// picks the right backend; it also survives the synthetic parent headers some readers build without a timestamp.
/// Flat comes first because it is the authoritative tree before activation.
/// </remarks>
internal sealed class MigrationReadOnlyScopeProvider(IWorldStateScopeProvider flat, IWorldStateScopeProvider pbt, ISpecProvider specProvider)
    : IWorldStateScopeProvider, IDisposable
{
    private IWorldStateScopeProvider Select(BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        if (targetBlock is null) return flat.HasRoot(baseBlock, null) ? flat : pbt;
        return specProvider.GetSpec(targetBlock).IsEip8347Enabled ? pbt : flat;
    }

    public bool HasRoot(BlockHeader? baseBlock, BlockHeader? targetBlock) => Select(baseBlock, targetBlock).HasRoot(baseBlock, targetBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, BlockHeader? targetBlock, LocalMetrics metrics) =>
        Select(baseBlock, targetBlock).BeginScope(baseBlock, targetBlock, metrics);

    public void Dispose()
    {
        try { (flat as IDisposable)?.Dispose(); }
        finally { (pbt as IDisposable)?.Dispose(); }
    }
}
