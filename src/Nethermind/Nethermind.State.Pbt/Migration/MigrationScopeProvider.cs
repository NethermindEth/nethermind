// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
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
    IPbtConfig config,
    IStateHeaderProvider stateHeaderProvider,
    ILogManager logManager) : IWorldStateScopeProvider
{
    private readonly IWorldStateScopeProvider _flat = flat.GlobalWorldState;
    private readonly IWorldStateScopeProvider _mirror = new PbtMirrorScopeProvider(flat.GlobalWorldState, pbtManager, resourcePool, config, stateHeaderProvider, logManager);
    private readonly IWorldStateScopeProvider _pbt = pbt.GlobalWorldState;

    internal IWorldStateScopeProvider Select(BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        if (selector.IsBinary(baseBlock, targetBlock)) return _pbt;
        if (selector.PbtHas(baseBlock)) return _mirror;
        return selector.PbtAhead(baseBlock) ? _flat : _mirror;
    }

    public bool HasRoot(BlockHeader? baseBlock) => Select(baseBlock, null).HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) =>
        stateHeaderProvider.TryGetBaseBlock(targetBlock, out BlockHeader? parent) && Select(parent, targetBlock).HasRoot(parent);

    // The selected backend opens at the resolved parent directly: its own target resolution would repeat the lookup.
    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (stateHeaderProvider.TryGetBaseBlock(targetBlock, out BlockHeader? parent)) return Select(parent, targetBlock).TryBeginScope(parent, metrics, out scope);
        scope = null;
        return false;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        Select(baseBlock, null).TryBeginScope(baseBlock, metrics, out scope);
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
    private IWorldStateScopeProvider SelectForBase(BlockHeader? baseBlock) => flat.HasRoot(baseBlock) ? flat : pbt;

    private IWorldStateScopeProvider SelectForTarget(BlockHeader targetBlock) => specProvider.GetSpec(targetBlock).IsEip8347Enabled ? pbt : flat;

    public bool HasRoot(BlockHeader? baseBlock) => SelectForBase(baseBlock).HasRoot(baseBlock);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => SelectForTarget(targetBlock).HasStateForTargetBlock(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        SelectForTarget(targetBlock).TryBeginScopeAtTarget(targetBlock, metrics, out scope);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        SelectForBase(baseBlock).TryBeginScope(baseBlock, metrics, out scope);

    public void Dispose()
    {
        try { (flat as IDisposable)?.Dispose(); }
        finally { (pbt as IDisposable)?.Dispose(); }
    }
}
