// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

using Nethermind.BeaconChain.Spec;
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
/// <para/>
/// Post-Gloas states live in a tier of their own (<see cref="RetainGloas"/>): every recent Gloas
/// block's post-state, not only epoch boundaries, because an execution payload envelope is verified
/// against the frozen post-state of exactly the block it names, one slot after that block. There is
/// no store fallback for this tier: <see cref="BeaconChainStore"/> snapshots decode as
/// <see cref="BeaconStateFulu"/> only, so a Gloas root that has aged out is unknown, not mis-typed.
/// </remarks>
internal sealed class PostStateCache(BeaconChainStore store, BeaconChainSpec spec, Hash256 lineageRoot, BeaconStateFulu lineageState) : IForkChoiceStateProvider, IGloasBlockStateProvider
{
    private const int RetainedStateCount = 8;

    // Two epochs of slots: an envelope arrives within its block's slot, and a payload attestation
    // or late envelope for a block two epochs back is already outside any window the spec honors.
    private const int RetainedGloasStateCount = 2 * (int)Presets.SlotsPerEpoch;

    private readonly LruCache<Hash256, BeaconStateFulu> _retained = new(RetainedStateCount, nameof(PostStateCache));
    private readonly LruCache<Hash256, BeaconStateGloas> _retainedGloas = new(RetainedGloasStateCount, nameof(PostStateCache) + "Gloas");

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
            BeaconStateFulu state = BeaconStateCodec.Decode(ssz, spec);
            _retained.Set(blockRoot, state);
            return state;
        }

        return null;
    }

    /// <inheritdoc/>
    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

    /// <summary>
    /// Retains a Gloas block's post-state under its block root, frozen: the state must not be
    /// mutated afterwards, or an envelope for that block will be verified against a state its
    /// builder never saw. A lineage that keeps advancing in place retains a clone, not itself.
    /// </summary>
    public void RetainGloas(Hash256 blockRoot, BeaconStateGloas state) => _retainedGloas.Set(blockRoot, state);

    /// <inheritdoc/>
    public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) =>
        _retainedGloas.TryGet(blockRoot, out BeaconStateGloas? retained) ? retained : null;
}
