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
    internal void OnGetChunks(LeanPeer peer, GetChunksMessage message)
    {
        bool enabled = IsEnabled;
        Outbox outbox = new();
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
            // A held descriptor's profile and size are checked against the peer's Status before serving.
            else if (Array.IndexOf(peer.Status.Profiles, entry.Descriptor.ProfileId) < 0)
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
                if (_clock.GetElapsedTime(started) >= LeanLimits.RequestAge)
                {
                    stopped = true;
                    break;
                }
                int offset = index * LeanProtocol.ChunkBytes;
                ChunkMessage chunk = new(serving.Id, serving.ObjectId, index,
                    entry.Body.AsMemory(offset, entry.Descriptor.ChunkLength(index)));
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(LeanLimits.RequestIdle);
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

        Outbox outbox = new();
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
                    LeanMetrics.Record(LeanEvent.Served);
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
}
