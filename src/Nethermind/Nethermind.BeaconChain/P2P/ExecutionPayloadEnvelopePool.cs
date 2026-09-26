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

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// In-memory cache of verified Gloas execution payload envelopes, serving
/// <c>ExecutionPayloadEnvelopesByRange</c>/<c>ByRoot</c>.
/// </summary>
/// <remarks>
/// A bounded recent-envelope cache only, evicted under capacity pressure with no epoch-based
/// retention guarantee, mirroring <see cref="DataColumnSidecarPool"/>'s own caveat. Only envelopes
/// that passed every check, their signature included, may be added: both protocols serve what it holds.
/// </remarks>
/// <param name="capacity">The most envelopes held.</param>
/// <param name="store">Where <see cref="GetCanonical"/> reads the chain from; <c>null</c> serves no range.</param>
/// <param name="status">Where <see cref="GetCanonical"/> reads the head from; <c>null</c> serves no range.</param>
public sealed class ExecutionPayloadEnvelopePool(int capacity = 1 << 12, BeaconChainStore? store = null, IBeaconChainStatusSource? status = null)
{
    /// <summary>The most blocks <see cref="GetCanonical"/> walks back from the head; not a spec value.</summary>
    internal const int MaxCanonicalWalk = 8192;

    private readonly LruCache<Hash256, SignedExecutionPayloadEnvelope> _byRoot = new(capacity, "execution payload envelopes");

    // A stored block never changes, so a flood of range requests decodes each block once, not once per request. Twice the walk,
    // so one full walk plus fork entries never evicts the head, which would make every repeat of that walk decode all of it again.
    private readonly LruCache<Hash256, BlockLink> _links = new(2 * MaxCanonicalWalk, "execution payload envelope chain links");

    public void Add(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope) => _byRoot.Set(blockRoot, envelope);

    public bool TryGet(Hash256 blockRoot, out SignedExecutionPayloadEnvelope? envelope) =>
        _byRoot.TryGet(blockRoot, out envelope);

    /// <summary>The held envelopes of the chain ending at the current head whose block slot is in <c>[startSlot, startSlot + count)</c>, in slot order.</summary>
    /// <remarks>
    /// gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange v1: envelopes come from the current fork choice view, never for a
    /// block whose payload the chain treats as empty. A block's payload is on the chain when its child's
    /// <c>bid.parent_block_hash</c> equals its own <c>bid.block_hash</c>; the head has no child, so its envelope is served
    /// when the pool holds it, which only a verified envelope reaches. The chain is walked through parent roots from the head,
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

            bool payloadOnChain = child is null || child.ParentBlockHash == link.BlockHash;
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
