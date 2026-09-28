// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// Verified Gloas execution payload envelopes, serving <c>ExecutionPayloadEnvelopesByRange</c>/<c>ByRoot</c>.
/// </summary>
/// <remarks>
/// With a store, every added envelope is persisted there and a read that misses the bounded in-memory
/// cache falls through to it, so eviction and restarts lose nothing the store still retains; the store's
/// <see cref="BeaconChainStore.PruneExecutionPayloadEnvelopes"/> bounds that retention. Only envelopes
/// that passed every check, their signature included, may be added: both protocols serve what it holds.
/// </remarks>
/// <param name="capacity">The most envelopes held in memory.</param>
/// <param name="store">Where envelopes are persisted and <see cref="GetCanonical"/> reads the chain from; <c>null</c> keeps envelopes in memory only and serves no range.</param>
/// <param name="status">Where <see cref="GetCanonical"/> reads the head from; <c>null</c> serves no range.</param>
/// <param name="logManager">Reports stored envelopes that cannot be read.</param>
public sealed class ExecutionPayloadEnvelopePool(int capacity = 1 << 12, BeaconChainStore? store = null, IBeaconChainStatusSource? status = null, ILogManager? logManager = null)
{
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<ExecutionPayloadEnvelopePool>();

    /// <summary>The most blocks <see cref="GetCanonical"/> walks back from the head; not a spec value.</summary>
    internal const int MaxCanonicalWalk = 8192;

    private readonly LruCache<Hash256, SignedExecutionPayloadEnvelope> _byRoot = new(capacity, "execution payload envelopes");

    // A corrupt record's header can claim up to MAX_PAYLOAD_SIZE, so each repeat request for it would allocate that much again.
    private readonly LruKeyCache<Hash256> _unreadable = new(256, "unreadable execution payload envelopes");

    // A stored block never changes, so a flood of range requests decodes each block once, not once per request. Twice the walk,
    // so one full walk plus fork entries never evicts the head, which would make every repeat of that walk decode all of it again.
    private readonly LruCache<Hash256, BlockLink> _links = new(2 * MaxCanonicalWalk, "execution payload envelope chain links");

    /// <exception cref="ArgumentException">The envelope has no payload, or names a beacon block other than <paramref name="blockRoot"/>.</exception>
    public void Add(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope)
    {
        if (store is null)
        {
            _ = BeaconChainStore.GetExecutionPayloadEnvelopeSlot(blockRoot, envelope);
        }
        else
        {
            store.PutExecutionPayloadEnvelope(blockRoot, envelope);
        }

        _byRoot.Set(blockRoot, envelope);
        _unreadable.Delete(blockRoot);
    }

    /// <remarks>A stored envelope that cannot be read is reported once and treated as not held, so it is never served, and is not read again until a later <see cref="Add"/> replaces it.</remarks>
    public bool TryGet(Hash256 blockRoot, out SignedExecutionPayloadEnvelope? envelope)
    {
        if (_byRoot.TryGet(blockRoot, out envelope))
        {
            return true;
        }

        try
        {
            if (store is null || _unreadable.Get(blockRoot) || !store.TryGetExecutionPayloadEnvelope(blockRoot, out envelope))
            {
                return false;
            }
        }
        catch (InvalidDataException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Stored execution payload envelope {blockRoot} is unreadable and is not served: {e.Message}");
            _unreadable.Set(blockRoot);
            envelope = null;
            return false;
        }

        _byRoot.Set(blockRoot, envelope);
        return true;
    }

    /// <summary>The held envelopes of the chain ending at the current head whose block slot is in <c>[startSlot, startSlot + count)</c>, in slot order.</summary>
    /// <remarks>
    /// gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange v1: envelopes come from the current fork choice view, never for a
    /// block whose payload the chain treats as empty. A block's payload is on the chain when its child's
    /// <c>bid.parent_block_hash</c> equals its own <c>bid.block_hash</c>. The head's payload is on the chain only when its fork
    /// choice node is <c>PAYLOAD_STATUS_FULL</c>; fork choice does not report that yet, so the head's envelope is never served,
    /// because a held envelope does not make the head FULL. The chain is walked through parent roots from the head,
    /// not read from the canonical slot index, because that index can keep a reorged-out root at a slot the head chain skips.
    /// </remarks>
    /// <exception cref="Eth2ReqRespException">Reaching <paramref name="startSlot"/> takes more than <see cref="MaxCanonicalWalk"/> blocks (<c>ResourceUnavailable</c>), or a stored block on the chain is unreadable (<c>ServerError</c>).</exception>
    public IEnumerable<SignedExecutionPayloadEnvelope> GetCanonical(ulong startSlot, ulong count)
    {
        if (store is null || status?.CurrentStatus is not { HeadRoot: { } headRoot, HeadSlot: var headSlot } || count == 0 || startSlot > headSlot)
        {
            return [];
        }

        List<SignedExecutionPayloadEnvelope> served = [];
        Hash256 root = headRoot;
        BlockLink? child = null;
        for (int walked = 0; ; walked++)
        {
            // Checked before the read, so a walk past the cap never decodes more than the cap.
            if (walked == MaxCanonicalWalk)
            {
                throw new Eth2ReqRespException($"Range start {startSlot} is more than {MaxCanonicalWalk} blocks below the head", ReqRespFraming.ResponseCode.ResourceUnavailable);
            }

            if (!TryGetLink(root, out BlockLink? link) || !link.IsGloas || link.Slot < startSlot)
            {
                break;
            }

            bool payloadOnChain = child is not null && child.ParentBlockHash == link.BlockHash;
            if (link.Slot - startSlot < count && payloadOnChain && TryGet(root, out SignedExecutionPayloadEnvelope? envelope))
            {
                served.Add(envelope!);
            }

            if (link.Slot == startSlot)
            {
                break;
            }

            child = link;
            root = link.ParentRoot;
        }

        served.Reverse();
        return served;
    }

    private bool TryGetLink(Hash256 root, [NotNullWhen(true)] out BlockLink? link)
    {
        if (_links.TryGet(root, out link))
        {
            return true;
        }

        ForkedSignedBeaconBlock? block;
        try
        {
            if (!store!.TryGetForkedBlock(root, out block))
            {
                return false;
            }
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException)
        {
            throw new Eth2ReqRespException($"Unreadable stored block {root}: {e.Message}", ReqRespFraming.ResponseCode.ServerError);
        }

        link = block is ForkedSignedBeaconBlock.OfGloas { Block.Message.Body.SignedExecutionPayloadBid.Message: { } bid }
            ? new BlockLink(block.Slot, block.ParentRoot, true, bid.BlockHash, bid.ParentBlockHash)
            : new BlockLink(block.Slot, block.ParentRoot, false, null, null);
        _links.Set(root, link);
        return true;
    }

    /// <summary>What the canonical walk reads of a stored block; the bid hashes are <c>null</c> for a pre-Gloas block.</summary>
    private sealed record BlockLink(ulong Slot, Hash256 ParentRoot, bool IsGloas, Hash256? BlockHash, Hash256? ParentBlockHash);
}
