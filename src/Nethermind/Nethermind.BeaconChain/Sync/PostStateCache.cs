// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// The block post-states retained for fork choice (the spec store's <c>block_states</c>), backed by
/// three tiers: the live canonical-lineage state, an LRU of cloned epoch-boundary and fork-branch
/// states, and the state snapshots persisted in the <see cref="BeaconChainStore"/>.
/// </summary>
/// <remarks>
/// Fork choice only ever resolves states of checkpoint roots (justified balances, target checkpoint
/// states), which are epoch-boundary blocks of recent epochs, so a small LRU of the states retained
/// around epoch boundaries suffices. States falling out of all tiers make the corresponding (old or
/// exotic-fork) roots unprocessable, which is acceptable below finality. Not thread-safe; owned by
/// the import worker.
/// </remarks>
internal sealed class PostStateCache(BeaconChainStore store, Hash256 lineageRoot, BeaconStateFulu lineageState) : IForkChoiceStateProvider
{
    private const int RetainedStateCount = 8;

    private readonly LruCache<Hash256, BeaconStateFulu> _retained = new(RetainedStateCount, nameof(PostStateCache));

    /// <summary>The root of the block whose post-state is <see cref="LineageState"/>.</summary>
    public Hash256 LineageRoot { get; private set; } = lineageRoot;

    /// <summary>The live state of the followed lineage, advanced in place as canonical blocks import.</summary>
    public BeaconStateFulu LineageState { get; private set; } = lineageState;

    /// <summary>Replaces the lineage with a new (root, state) pair, e.g. after adopting a reorged head.</summary>
    public void SetLineage(Hash256 root, BeaconStateFulu state)
    {
        LineageRoot = root;
        LineageState = state;
    }

    /// <summary>Adds a post-state to the retained LRU. The state must not be mutated afterwards.</summary>
    public void Retain(Hash256 blockRoot, BeaconStateFulu state) => _retained.Set(blockRoot, state);

    /// <inheritdoc/>
    public BeaconStateFulu? GetBlockState(Hash256 blockRoot)
    {
        if (blockRoot == LineageRoot)
        {
            return LineageState;
        }

        if (_retained.TryGet(blockRoot, out BeaconStateFulu? retained))
        {
            return retained;
        }

        if (store.TryGetState(blockRoot, out byte[]? ssz))
        {
            BeaconStateFulu.Decode(ssz, out BeaconStateFulu state);
            _retained.Set(blockRoot, state);
            return state;
        }

        return null;
    }

    /// <inheritdoc/>
    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();
}
