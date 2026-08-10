// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.State;
using Nethermind.State.Flat.History;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.FlatHistory;

/// <summary>
/// devp2p-backed <see cref="IWindowImportSource"/>: wraps one peer's <see cref="INHistSyncPeer.GetChangesets"/>
/// client method and pages it into the whole-range stream <see cref="PeerFedWindowImporter"/> expects. The wire
/// call is range-bounded with no chunk-level cursor (<c>GetChangesetsMessage</c> carries only
/// <c>FromBlock</c>/<c>ToBlock</c>), so a response that ends mid-block (the server's byte/chunk cap landed inside
/// a still-open block) cannot be resumed from where it stopped - the only correct move is to re-request the same
/// block from scratch and buffer its chunks until <see cref="ChangesetChunkEntry.IsLastChunkForBlock"/> is seen,
/// only then yielding them downstream. <see cref="PeerFedWindowImporter"/>'s own chunk-gap guard
/// (<c>BlockStreamCursor.AdvanceTo</c>) would otherwise reject a restarted chunk-0 for a block it had already
/// partially consumed.
/// </summary>
public sealed class NHistWindowImportSource(PeerInfo peer, INHistSyncPeer syncPeer) : IWindowImportSource
{
    public PeerInfo Peer => peer;

    public async IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(
        ulong fromBlockInclusive, ulong toBlockInclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ulong resumeBlock = fromBlockInclusive;
        int consecutiveIncompleteAttempts = 0;

        while (resumeBlock <= toBlockInclusive)
        {
            NHistChangesetsPage page = await syncPeer.GetChangesets(resumeBlock, toBlockInclusive, cancellationToken);
            if (page.Chunks.Count == 0) yield break;

            List<WindowImportChunk> pending = [];
            ulong pendingBlock = resumeBlock;
            bool completedAny = false;

            foreach (ChangesetChunkEntry chunk in page.Chunks)
            {
                pendingBlock = chunk.Block;
                pending.Add(new WindowImportChunk(chunk.Block, chunk.ChunkIndex, chunk.IsLastChunkForBlock, chunk.Payload));

                if (!chunk.IsLastChunkForBlock) continue;

                foreach (WindowImportChunk done in pending) yield return done;
                pending.Clear();
                resumeBlock = chunk.Block + 1;
                completedAny = true;
                consecutiveIncompleteAttempts = 0;
            }

            if (pending.Count == 0) continue;

            // The response ended mid-block for pendingBlock: re-request that exact block from scratch next
            // iteration. Two such attempts in a row with zero forward progress means the block's own changeset
            // alone exceeds a single response's byte/chunk cap - the wire protocol has no chunk-level resume
            // cursor to recover from that, so this fails closed instead of looping forever.
            if (!completedAny && ++consecutiveIncompleteAttempts >= 2)
            {
                throw new NHistChangesetOversizedBlockException(pendingBlock, peer.ToString());
            }

            resumeBlock = pendingBlock;
        }
    }
}

public sealed class NHistChangesetOversizedBlockException(ulong block, string peerDescription)
    : System.Exception($"Block {block}'s changeset exceeds the max response size nhist1 peer {peerDescription} will ever return in one page; " +
                        "the wire protocol has no chunk-level resume cursor to recover from this.");
