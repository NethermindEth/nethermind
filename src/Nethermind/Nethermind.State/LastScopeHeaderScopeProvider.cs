// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.State;

/// <summary>
/// Scope provider for overridable world states: remembers the header of the scope last opened and lets the
/// wrapped provider resolve a target's parent from it before the block tree.
/// </summary>
/// <remarks>
/// An overridable env commits state overrides into an in-memory header the block tree never sees, and a
/// target processed through <see cref="TryBeginScope"/> becomes the committed state its child builds on.
/// Matching is by <see cref="BlockHeader.ParentHash"/> only; whether the remembered header's state exists
/// remains the wrapped provider's <c>HasRoot</c> check.
/// </remarks>
public sealed class LastScopeHeaderScopeProvider : IWorldStateScopeProvider
{
    private readonly LastScopeHeaderProvider _headerProvider;
    private readonly IWorldStateScopeProvider _baseProvider;

    /// <param name="createBaseProvider">
    /// Builds the wrapped provider over the header provider that serves the remembered header first.
    /// </param>
    public LastScopeHeaderScopeProvider(IStateHeaderProvider stateHeaderProvider, Func<IStateHeaderProvider, IWorldStateScopeProvider> createBaseProvider)
    {
        _headerProvider = new LastScopeHeaderProvider(stateHeaderProvider);
        _baseProvider = createBaseProvider(_headerProvider);
    }

    public bool HasRoot(BlockHeader? baseBlock) => _baseProvider.HasRoot(baseBlock);

    public bool HasStateForTarget(BlockHeader targetBlock) => _baseProvider.HasStateForTarget(targetBlock);

    public bool TryBeginScope(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!_baseProvider.TryBeginScope(targetBlock, metrics, out scope)) return false;
        _headerProvider.LastScopeHeader = targetBlock;
        return true;
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        _headerProvider.LastScopeHeader = baseBlock;
        return _baseProvider.BeginScope(baseBlock, metrics);
    }

    private sealed class LastScopeHeaderProvider(IStateHeaderProvider stateHeaderProvider) : IStateHeaderProvider
    {
        public BlockHeader? LastScopeHeader { get; set; }

        public BlockHeader? FindParentHeader(BlockHeader target) =>
            LastScopeHeader is { } lastScopeHeader && target.ParentHash == lastScopeHeader.Hash
                ? lastScopeHeader
                : stateHeaderProvider.FindParentHeader(target);

        public ulong FinalizedBlockNumber => stateHeaderProvider.FinalizedBlockNumber;

        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => stateHeaderProvider.GetFinalizedHeader(blockNumber);
    }
}
