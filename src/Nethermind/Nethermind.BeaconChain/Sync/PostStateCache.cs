// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

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
/// against the frozen post-state of exactly the block it names, one slot after that block. Gloas
/// checkpoint candidates are also kept in a small epoch-boundary tier, as the Fulu ones are in the LRU
/// above, so a justified root resolved first epochs later has not aged out with the per-block tier.
/// The finalized Gloas checkpoint's state is pinned (<see cref="PinGloas"/>) and never ages out, and so are the two
/// latest justified ones, of which the next finalized checkpoint is one: before a checkpoint candidate can evict a
/// boundary entry, the root <paramref name="justifiedRoot"/> names is pinned while its state is still held. A persisted
/// Gloas state is read back from the store only for a root <paramref name="isGloasBlock"/> accepts, into the per-block
/// tier, so the read never evicts a checkpoint candidate unpinned. An undecodable persisted state of either fork is logged and treated as absent.
/// A checkpoint candidate evicted from the boundary tier while <paramref name="isAboveFinalized"/> holds is persisted,
/// because a root first justified after leaving both tiers is resolvable only from the store. With finality two or three
/// epochs behind, a canonical candidate is usually finalized before eight later ones evict it; under non-finality this
/// writes about one state per epoch beyond the tier's span. The store has no state deletion, so these states stay after finality.
/// <para/>
/// A node started from a Gloas anchor has no Fulu lineage: <see cref="LineageRoot"/> and
/// <see cref="LineageState"/> are then <c>null</c>.
/// </remarks>
/// <param name="isGloasBlock">Whether fork choice holds the root at a Gloas slot; bounds which roots may cost a store read.</param>
/// <param name="justifiedRoot">Fork choice's justified checkpoint root, whose Gloas state must outlive the boundary tier until finalization passes it.</param>
/// <param name="isAboveFinalized">Whether fork choice holds the root above the finalized checkpoint's start slot, so it can still become justified.</param>
internal sealed class PostStateCache(
    BeaconChainStore store,
    BeaconChainSpec spec,
    Hash256? lineageRoot,
    BeaconStateFulu? lineageState,
    Func<Hash256, bool>? isGloasBlock = null,
    Func<Hash256>? justifiedRoot = null,
    ILogManager? logManager = null,
    Func<Hash256, bool>? isAboveFinalized = null) : IForkChoiceStateProvider, IGloasBlockStateProvider
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<PostStateCache>();

    private const int RetainedStateCount = 8;

    /// <summary>Byte offset of <c>slot</c> in a persisted state, the same in the Fulu and Gloas layouts: <c>genesis_time</c> (8) plus <c>genesis_validators_root</c> (32).</summary>
    private const int StateSlotOffset = 40;

    // Two epochs of slots: an envelope arrives within its block's slot, and a payload attestation
    // or late envelope for a block two epochs back is already outside any window the spec honors.
    private const int RetainedGloasStateCount = 2 * (int)Presets.SlotsPerEpoch;

    private readonly LruCache<Hash256, BeaconStateFulu> _retained = new(RetainedStateCount, nameof(PostStateCache));
    private readonly LruCache<Hash256, BeaconStateGloas> _retainedGloas = new(RetainedGloasStateCount, nameof(PostStateCache) + "Gloas");
    private readonly GloasBoundaryTier _retainedGloasBoundaries = new(store, isAboveFinalized, (logManager ?? LimboLogs.Instance).GetClassLogger<PostStateCache>());

    private Hash256? _pinnedGloasRoot;
    private BeaconStateGloas? _pinnedGloasState;
    private Hash256? _pinnedJustifiedRoot;
    private BeaconStateGloas? _pinnedJustifiedState;
    private Hash256? _previousJustifiedRoot;
    private BeaconStateGloas? _previousJustifiedState;

    /// <summary>The root of the block whose post-state is <see cref="LineageState"/>; <c>null</c> without a Fulu lineage.</summary>
    public Hash256? LineageRoot { get; private set; } = lineageRoot;

    /// <summary>The live state of the followed lineage, advanced in place as canonical blocks import; <c>null</c> without a Fulu lineage.</summary>
    public BeaconStateFulu? LineageState { get; private set; } = lineageState;

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

        // A Gloas snapshot is not this root's Fulu state, so it is refused by its slot before a full decode; the Gloas getter serves it.
        if (store.TryGetState(blockRoot, out byte[]? ssz) && !IsGloasSnapshot(ssz) && DecodePersisted(blockRoot, ssz) is ForkedBeaconState.OfFulu { State: BeaconStateFulu state })
        {
            _retained.Set(blockRoot, state);
            return state;
        }

        return null;
    }

    private bool IsGloasSnapshot(byte[] ssz) =>
        ssz.Length >= StateSlotOffset + sizeof(ulong) && SignedBeaconBlockCodec.IsGloasSlot(BinaryPrimitives.ReadUInt64LittleEndian(ssz.AsSpan(StateSlotOffset)), spec);

    /// <inheritdoc/>
    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

    /// <summary>
    /// Retains a Gloas block's post-state under its block root, frozen: the state must not be
    /// mutated afterwards, or an envelope for that block will be verified against a state its
    /// builder never saw. A lineage that keeps advancing in place retains a clone, not itself.
    /// </summary>
    /// <param name="checkpointCandidate">Whether the block can be an epoch's checkpoint block, so its state also goes to the epoch-boundary tier.</param>
    public void RetainGloas(Hash256 blockRoot, BeaconStateGloas state, bool checkpointCandidate = false)
    {
        _retainedGloas.Set(blockRoot, state);
        if (checkpointCandidate)
        {
            PinJustified();
            _retainedGloasBoundaries.Set(blockRoot, new RetainedGloasState(blockRoot, state));
        }
    }

    /// <summary>Pins the finalized Gloas checkpoint's post-state, replacing the previous pin; the state must not be mutated afterwards.</summary>
    /// <remarks>Also releases the outgoing justified state, which was kept only until finalization moved.</remarks>
    public void PinGloas(Hash256 blockRoot, BeaconStateGloas state)
    {
        _pinnedGloasRoot = blockRoot;
        _pinnedGloasState = state;
        _previousJustifiedRoot = null;
        _previousJustifiedState = null;
    }

    /// <summary>specs/gloas/fork-choice.md <c>on_attester_slashing</c> reads <c>store.block_states[store.justified_checkpoint.root]</c> at any time before finality.</summary>
    /// <remarks>
    /// The outgoing justified state stays pinned one justification longer: specs/phase0/beacon-chain.md
    /// <c>weigh_justification_and_finalization</c> finalizes the previous or the current justified checkpoint, possibly in
    /// the same import that moves the justified checkpoint past it and before <c>OnFinalized</c> reads its state.
    /// </remarks>
    private void PinJustified()
    {
        if (justifiedRoot?.Invoke() is not { } root || root == _pinnedJustifiedRoot)
        {
            return;
        }

        if (GetGloasBlockState(root) is { } state)
        {
            _previousJustifiedRoot = _pinnedJustifiedRoot;
            _previousJustifiedState = _pinnedJustifiedState;
            _pinnedJustifiedRoot = root;
            _pinnedJustifiedState = state;
        }
    }

    /// <inheritdoc/>
    public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot)
    {
        if (blockRoot == _pinnedGloasRoot)
        {
            return _pinnedGloasState;
        }

        if (blockRoot == _pinnedJustifiedRoot)
        {
            return _pinnedJustifiedState;
        }

        if (blockRoot == _previousJustifiedRoot)
        {
            return _previousJustifiedState;
        }

        if (_retainedGloas.TryGet(blockRoot, out BeaconStateGloas? retained))
        {
            return retained;
        }

        if (_retainedGloasBoundaries.TryGet(blockRoot, out RetainedGloasState boundary))
        {
            return boundary.State;
        }

        if (isGloasBlock?.Invoke(blockRoot) == true
            && store.TryGetState(blockRoot, out byte[]? ssz)
            && DecodePersisted(blockRoot, ssz) is ForkedBeaconState.OfGloas { State: BeaconStateGloas state })
        {
            _retainedGloas.Set(blockRoot, state);
            return state;
        }

        return null;
    }

    private ForkedBeaconState? DecodePersisted(Hash256 blockRoot, byte[] ssz)
    {
        try
        {
            return BeaconStateCodec.DecodeForked(ssz, spec);
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException or NotSupportedException)
        {
            // A corrupt local snapshot must not fail every import or envelope naming this root.
            if (_logger.IsWarn) _logger.Warn($"Ignoring the undecodable persisted state of {blockRoot}: {e.Message}");
            return null;
        }
    }

    private readonly record struct RetainedGloasState(Hash256 Root, BeaconStateGloas State);

    /// <summary>The Gloas epoch-boundary tier, which persists a checkpoint candidate it evicts while that root can still become justified.</summary>
    private sealed class GloasBoundaryTier(BeaconChainStore store, Func<Hash256, bool>? isAboveFinalized, ILogger logger)
        : LruCache<Hash256, RetainedGloasState>(RetainedStateCount, nameof(PostStateCache) + "GloasBoundaries")
    {
        protected override void Evict(RetainedGloasState evicted)
        {
            // Also raised when a retained root is set again, which evicts nothing.
            if (Contains(evicted.Root) || isAboveFinalized?.Invoke(evicted.Root) != true)
            {
                return;
            }

            store.PutState(evicted.Root, BeaconStateGloas.Encode(evicted.State));
            if (logger.IsDebug) logger.Debug($"Persisted the evicted Gloas checkpoint candidate state of {evicted.Root} at slot {evicted.State.Slot}");
        }
    }
}
