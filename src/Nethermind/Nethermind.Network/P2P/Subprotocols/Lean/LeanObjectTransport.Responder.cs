// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public sealed partial class LeanObjectTransport
{
    // Request ID, both list prefixes and slack for the largest prefix forms.
    private const int ResponseOverhead = 32;
    private const int NonOkResultBytes = 3;
    private const int MaxOnDemandBuilds = 2;
    private const ulong MaxOnDemandDepth = 64;

    internal void OnGetObjects(LeanPeer peer, GetObjectsMessage message)
    {
        Outbox outbox = new(_clock);
        bool accepted;
        lock (_gate) accepted = !peer.Closed && CheckIncomingId(peer, message.RequestId, outbox);
        if (!accepted) { outbox.Flush(); return; }
        // Block proofs are built from the block tree on demand, outside the lock, since hashing a proof is not cheap.
        int builds = 0;
        foreach (LeanSelector selector in message.Selectors)
            if (selector.Kind == LeanProtocol.KindBlockProof && selector.ProfileId == LocalProfile
                && builds < MaxOnDemandBuilds && EnsureBlockProof(selector)) builds++;

        bool enabled = IsEnabled;
        lock (_gate)
        {
            if (peer.Closed)
            {
                outbox.Flush();
                return;
            }
            bool busy = !enabled || peer.Node.ServingRequests >= LeanProtocol.MaxRequestsPerPeer;
            LeanSelector[] selectors = message.Selectors;
            LeanObjectResult[] results = new LeanObjectResult[selectors.Length];
            int remaining = LeanProtocol.MaxMetadataResponseBytes - ResponseOverhead - selectors.Length * NonOkResultBytes;
            for (int i = 0; i < selectors.Length; i++)
            {
                LeanSelector selector = selectors[i];
                LeanObjectResult result;
                if (busy) result = LeanObjectResult.Of(LeanResultStatus.Busy);
                else if (selector.ProfileId != LocalProfile || (LocalKinds & (1 << (selector.Kind - 1))) == 0)
                    result = LeanObjectResult.Of(LeanResultStatus.Unsupported);
                else if (!_store.TryLookup(selector, out LeanObjectStore.Entry entry)) result = LeanObjectResult.Of(LeanResultStatus.Unavailable);
                else if (entry.Descriptor.ByteLength > peer.Status.MaxObjectBytes) result = LeanObjectResult.Of(LeanResultStatus.TooLarge);
                else
                {
                    result = new(LeanResultStatus.Ok, entry.Descriptor);
                    // OK results that would overflow the response are returned as Busy rather than omitted or split.
                    int length = ObjectsMessageSerializer.EncodeResult(result).Length - NonOkResultBytes;
                    if (length > remaining) result = LeanObjectResult.Of(LeanResultStatus.Busy);
                    else remaining -= length;
                }
                results[i] = result;
            }
            outbox.Send(peer, new ObjectsMessage(message.RequestId, results));
        }
        outbox.Flush();
    }

    internal void OnGetChunks(LeanPeer peer, GetChunksMessage message)
    {
        bool enabled = IsEnabled;
        Outbox outbox = new(_clock);
        LeanServing? serving = null;
        LeanObjectStore.Entry? entry = null;
        lock (_gate)
        {
            if (peer.Closed || !CheckIncomingId(peer, message.RequestId, outbox))
            {
                outbox.Flush();
                return;
            }
            LeanCompleteStatus? status = null;
            if (!enabled || peer.Node.ServingRequests >= LeanProtocol.MaxRequestsPerPeer) status = LeanCompleteStatus.Busy;
            else if (!_store.TryGet(message.ObjectId, out entry)) status = LeanCompleteStatus.Unavailable;
            else if (message.Indices[^1] >= entry.Descriptor.ChunkCount)
            {
                Violation(peer, $"chunk index {message.Indices[^1]} is not below N = {entry.Descriptor.ChunkCount}", outbox);
                outbox.Flush();
                return;
            }
            // A held descriptor's profile, kind and size are checked against the peer's Status before serving.
            else if (!peer.Status.SupportsKind(entry.Descriptor.Kind) || Array.IndexOf(peer.Status.Profiles, entry.Descriptor.ProfileId) < 0)
                status = LeanCompleteStatus.Unsupported;
            else if (entry.Descriptor.ByteLength > peer.Status.MaxObjectBytes) status = LeanCompleteStatus.TooLarge;
            else
            {
                serving = new(message.RequestId, message.ObjectId);
                peer.Serving[message.RequestId] = serving;
            }
            if (status is { } refused) outbox.Send(peer, new CompleteMessage(message.RequestId, message.ObjectId, refused));
        }
        outbox.Flush();
        if (serving is not null) _ = ServeAsync(peer, serving, entry!, message.Indices);
    }

    /// <summary>Writes requested chunks one message at a time behind connection backpressure, then the terminal Complete.</summary>
    private async Task ServeAsync(LeanPeer peer, LeanServing serving, LeanObjectStore.Entry entry, int[] indices)
    {
        CancellationToken cancellation = serving.Cancellation.Token;
        long started = _clock.GetTimestamp();
        int written = 0;
        bool stopped = false;
        try
        {
            foreach (int index in indices)
            {
                if (cancellation.IsCancellationRequested) break;
                // The responder must stop serving by the request's absolute deadline.
                if (_clock.GetElapsedTime(started) >= LeanProtocol.MaxRequestAge)
                {
                    stopped = true;
                    break;
                }
                int offset = index * LeanProtocol.ChunkBytes;
                ChunkMessage chunk = new(serving.Id, serving.ObjectId, index,
                    entry.Body.AsMemory(offset, entry.Descriptor.ChunkLength(index)));
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(LeanProtocol.MaxRequestIdle);
                if (!await peer.Link.SendChunkAsync(chunk, idle.Token).ConfigureAwait(false))
                {
                    stopped = true;
                    break;
                }
                written++;
                LeanMetrics.ChunkServed();
                Interlocked.Increment(ref _stats.ChunksServed);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Cancel or disconnection, or idle past the request deadline.
            stopped = !cancellation.IsCancellationRequested;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 serving {serving.ObjectId.ToShortString()} to {peer} stopped: {exception.Message}");
            stopped = true;
        }

        Outbox outbox = new(_clock);
        lock (_gate)
        {
            if (peer.Serving.TryGetValue(serving.Id, out LeanServing? current) && current == serving) peer.Serving.Remove(serving.Id);
            if (!serving.CompleteWritten && !peer.Closed)
            {
                serving.CompleteWritten = true;
                bool served = written == indices.Length;
                outbox.Send(peer, new CompleteMessage(serving.Id, serving.ObjectId, cancellation.IsCancellationRequested ? LeanCompleteStatus.Cancelled : served && !stopped ? LeanCompleteStatus.Served : LeanCompleteStatus.Busy));
                if (served)
                {
                    Interlocked.Increment(ref _stats.ObjectsServed);
                    LeanMetrics.Record(entry.Descriptor.Kind, LeanEvent.Served);
                }
            }
            serving.Cancellation.Dispose();
        }
        outbox.Flush();
    }

    internal void OnCancel(LeanPeer peer, CancelMessage message)
    {
        lock (_gate)
        {
            if (peer.Closed) return;
            // The serving task writes the terminal after any in-flight Chunk has unwound.
            if (peer.Serving.TryGetValue(message.RequestId, out LeanServing? serving) && !serving.CompleteWritten)
                serving.Cancellation.Cancel();
        }
    }

    /// <summary>Builds the sidecar of a recent canonical block on demand; true when a proof had to be hashed.</summary>
    /// <remarks>Only blocks this node validated itself are served, and only near the head, which bounds the hashing a peer can cause.</remarks>
    private bool EnsureBlockProof(in LeanSelector selector)
    {
        lock (_gate) if (_store.TryLookup(selector, out _)) return false;
        Hash256 hash = new(selector.LookupKey);
        if (_blockTree.FindHeader(hash, Blockchain.BlockTreeLookupOptions.TotalDifficultyNotNeeded | Blockchain.BlockTreeLookupOptions.DoNotCreateLevelIfMissing)
                is not { RecursiveStark: not null } header
            || _blockTree.Head is not { } head || header.Number > head.Number || head.Number - header.Number > MaxOnDemandDepth
            || !_blockTree.IsMainChain(header)) return false;
        if (_blockTree.FindBlock(hash, Blockchain.BlockTreeLookupOptions.TotalDifficultyNotNeeded) is { } block)
            PublishBlockProof(block, announce: false);
        return true;
    }
}
