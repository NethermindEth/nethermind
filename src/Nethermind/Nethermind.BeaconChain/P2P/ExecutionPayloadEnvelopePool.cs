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

    private readonly LruCache<Hash256, SignedExecutionPayloadEnvelope> _byRoot = new(capacity, "execution payload envelopes");

    // A corrupt record's header can claim up to MAX_PAYLOAD_SIZE, so each repeat request for it would allocate that much again.
    private readonly LruKeyCache<Hash256> _unreadable = new(256, "unreadable execution payload envelopes");

    /// <summary>How often <see cref="GetCanonical"/> reads the head and the range top again while a reorg keeps them apart; not a spec value.</summary>
    private const int MaxTopReads = 3;

    private readonly LruCache<Hash256, BlockLink> _links = new(1 << 14, "execution payload envelope chain links");

    /// <exception cref="ArgumentException">The envelope has no payload, names a beacon block other than <paramref name="blockRoot"/>, or, with a store, encodes to more than <c>MAX_PAYLOAD_SIZE</c> bytes.</exception>
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
    /// choice node is <c>PAYLOAD_STATUS_FULL</c> (<see cref="IBeaconChainStatusSource.CurrentHead"/>), never merely because an
    /// envelope is held. The canonical index selects the last requested block, then parent roots keep the reply on one chain.
    /// </remarks>
    /// <exception cref="Eth2ReqRespException">A stored block on the chain is unreadable (<c>ServerError</c>), or reorgs kept the
    /// payload status of the last requested block unknown while nothing else could be served (<c>ResourceUnavailable</c>).</exception>
    public IEnumerable<SignedExecutionPayloadEnvelope> GetCanonical(ulong startSlot, ulong count)
    {
        if (store is null || status is null || count == 0)
        {
            return [];
        }

        Hash256? root = null;
        BlockLink? child = null;
        Hash256? fullHeadRoot = null;
        ulong lastSlot = 0;
        bool resolved = false;
        for (int reads = 0; reads < MaxTopReads && !resolved; reads++)
        {
            // One read, so the head and its payload status always belong together.
            (StatusMessageV2 current, fullHeadRoot) = status.CurrentHead;
            if (current is not { HeadRoot: { } headRoot, HeadSlot: var headSlot } || startSlot > headSlot)
            {
                return [];
            }

            lastSlot = Math.Min(headSlot, count - 1 > ulong.MaxValue - startSlot ? ulong.MaxValue : startSlot + count - 1);
            root = ReadTop(startSlot, lastSlot, headRoot, out child, out resolved);
        }

        if (root is null) return [];
        Hash256 top = root;

        List<SignedExecutionPayloadEnvelope> served = [];
        while (true)
        {
            if (!TryGetLink(root, out BlockLink? link) || !link.IsGloas || link.Slot < startSlot)
            {
                break;
            }

            bool payloadOnChain = child is null ? fullHeadRoot == root : child.ParentBlockHash == link.BlockHash;
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

        // BeaconBlocksByRange: the first envelope in the range MUST be served if held, so an unknown top is not answered as none.
        if (!resolved && served.Count == 0 && TryGet(top, out _))
        {
            throw new Eth2ReqRespException($"Payload status of block {top} is unknown while fork choice reorgs", ReqRespFraming.ResponseCode.ResourceUnavailable);
        }

        served.Reverse();
        return served;
    }

    /// <summary>The last canonical block at or below <paramref name="lastSlot"/>, and its canonical child unless it is the head.</summary>
    /// <param name="resolved"><c>false</c> when the block is neither the head nor has a canonical child, as when a reorg moves the index
    /// under the published head; the block is then judged as a head that is not FULL, and every block below it by its own child's bid.</param>
    private Hash256? ReadTop(ulong startSlot, ulong lastSlot, Hash256 headRoot, out BlockLink? child, out bool resolved)
    {
        child = null;
        resolved = true;
        Hash256? root;
        for (ulong slot = lastSlot; ; slot--)
        {
            if (store!.TryGetCanonicalRoot(slot, out root) || slot == startSlot) break;
        }

        if (root is null || root == headRoot) return root;
        if (store.TryGetChildren(root, out Hash256[] children, out _))
        {
            foreach (Hash256 candidate in children)
            {
                if (TryGetLink(candidate, out BlockLink? candidateLink) && candidateLink.Slot > lastSlot &&
                    store.TryGetCanonicalRoot(candidateLink.Slot, out Hash256? canonical) && canonical == candidate)
                {
                    child = candidateLink;
                    return root;
                }
            }
        }

        resolved = false;
        return root;
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
