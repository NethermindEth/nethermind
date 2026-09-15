// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.State;

/// <summary>
/// Scope provider for overridable world states: keeps the headers of every scope it opened, keyed by hash,
/// and lets the wrapped provider resolve a target's parent from them before the block tree.
/// </summary>
/// <remarks>
/// An overridable env commits state overrides into an in-memory header the block tree never sees, and a
/// target processed through <see cref="TryBeginScopeAtTarget"/> becomes the committed state its child builds on.
/// Matching is by <see cref="BlockHeader.ParentHash"/> only; whether a known header's state exists remains
/// the wrapped provider's <c>HasRoot</c> check. <see cref="Clear"/> goes with the overrides reset that
/// discards the state those headers describe.
/// </remarks>
public sealed class KnownHeadersScopeProvider : IWorldStateScopeProvider
{
    private readonly KnownHeadersProvider _headerProvider;
    private readonly IWorldStateScopeProvider _baseProvider;

    /// <param name="createBaseProvider">
    /// Builds the wrapped provider over the header provider that serves the known headers first.
    /// </param>
    public KnownHeadersScopeProvider(IStateHeaderProvider stateHeaderProvider, Func<IStateHeaderProvider, IWorldStateScopeProvider> createBaseProvider)
    {
        _headerProvider = new KnownHeadersProvider(stateHeaderProvider);
        _baseProvider = createBaseProvider(_headerProvider);
    }

    public bool HasRoot(BlockHeader? baseBlock) => _baseProvider.HasRoot(baseBlock);

    public bool HasStateForTarget(BlockHeader targetBlock) => _baseProvider.HasStateForTarget(targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!_baseProvider.TryBeginScopeAtTarget(targetBlock, metrics, out scope)) return false;
        _headerProvider.Remember(targetBlock);
        return true;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (!_baseProvider.TryBeginScope(baseBlock, metrics, out scope)) return false;
        _headerProvider.Remember(baseBlock);
        return true;
    }

    public void Clear() => _headerProvider.Clear();

    private sealed class KnownHeadersProvider(IStateHeaderProvider stateHeaderProvider) : IStateHeaderProvider
    {
        private readonly Dictionary<Hash256, BlockHeader> _headers = [];

        public void Remember(BlockHeader? header)
        {
            if (header?.Hash is not null) _headers[header.Hash] = header;
        }

        public void Clear() => _headers.Clear();

        public BlockHeader? FindParentHeader(BlockHeader target) =>
            target.ParentHash is not null && _headers.TryGetValue(target.ParentHash, out BlockHeader? knownParent)
                ? knownParent
                : stateHeaderProvider.FindParentHeader(target);

        public ulong FinalizedBlockNumber => stateHeaderProvider.FinalizedBlockNumber;

        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => stateHeaderProvider.GetFinalizedHeader(blockNumber);
    }
}
