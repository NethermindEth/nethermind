// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
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
        // Block proofs are built from the block tree on demand, outside the lock, since hashing a proof is not cheap.
        int builds = 0;
        foreach (LeanSelector selector in message.Selectors)
            if (selector is { Kind: LeanProtocol.KindBlockProof, LookupKind: LeanProtocol.LookupPrimary } && selector.ProfileId == LocalProfile
                && builds < MaxOnDemandBuilds && EnsureBlockProof(selector)) builds++;

        bool enabled = IsEnabled;
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed || !CheckIncomingId(peer, message.RequestId, outbox))
            {
                outbox.Flush();
                return;
            }
            bool busy = !enabled || peer.ServingFull;
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
                    result = new(LeanResultStatus.Ok, entry.Descriptor, entry.Skeleton);
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
            if (!enabled || peer.ServingFull) status = LeanCompleteStatus.Busy;
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
                    entry.Body.AsMemory(offset, entry.Descriptor.ChunkLength(index)), entry.Tree.GetBranch(index));
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

        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Serving.TryGetValue(serving.Id, out LeanServing? current) && current == serving) peer.Serving.Remove(serving.Id);
            if (!serving.CompleteWritten && !stopped && !peer.Closed)
            {
                serving.CompleteWritten = true;
                bool served = written == indices.Length;
                outbox.Send(peer, new CompleteMessage(serving.Id, serving.ObjectId, served ? LeanCompleteStatus.Served : LeanCompleteStatus.Busy));
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
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            // Cancels for unknown or completed requests are ignored; other request types already wrote their response.
            if (peer.Serving.Remove(message.RequestId, out LeanServing? serving) && !serving.CompleteWritten)
            {
                serving.CompleteWritten = true;
                serving.Cancellation.Cancel();
                outbox.Send(peer, new CompleteMessage(message.RequestId, serving.ObjectId, LeanCompleteStatus.Cancelled));
            }
        }
        outbox.Flush();
    }

    internal void OnGetTransactions(LeanPeer peer, GetTransactionsMessage message)
    {
        bool busy;
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed || !CheckIncomingId(peer, message.RequestId, outbox))
            {
                outbox.Flush();
                return;
            }
            busy = !IsEnabled || peer.ServingFull;
        }

        ValueHash256[] hashes = message.Hashes;
        (LeanResultStatus, byte[])[] results = new (LeanResultStatus, byte[])[hashes.Length];
        int others = (hashes.Length - 1) * NonOkResultBytes;
        int remaining = LeanProtocol.MaxTxResponseBytes - ResponseOverhead - hashes.Length * NonOkResultBytes;
        for (int i = 0; i < hashes.Length; i++)
        {
            results[i] = (busy ? LeanResultStatus.Busy : LeanResultStatus.Unavailable, []);
            if (busy || FindEnvelope(hashes[i]) is not { } envelope) continue;
            int length = TransactionsMessageSerializer.EncodeResult(LeanResultStatus.Ok, envelope).Length - NonOkResultBytes;
            if (length + others + NonOkResultBytes > LeanProtocol.MaxTxResponseBytes - ResponseOverhead) results[i] = (LeanResultStatus.TooLarge, []);
            else if (length > remaining) results[i] = (LeanResultStatus.Busy, []);
            else
            {
                remaining -= length;
                results[i] = (LeanResultStatus.Ok, envelope);
            }
        }
        peer.Link.Send(new TransactionsMessage(message.RequestId, results));
    }

    /// <summary>Finds a canonical envelope in the pool or retained with a served wrapper; never searches history or asks peers.</summary>
    private byte[]? FindEnvelope(in ValueHash256 hash)
    {
        if (_txPool.TryGetPendingTransaction(hash, out Transaction? transaction))
        {
            byte[] envelope = TxDecoder.Instance.Encode(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes;
            if (ValueKeccak.Compute(envelope) == hash) return envelope;
        }
        lock (_gate) return _store.TryGetEnvelope(hash, out byte[] retained) ? retained : null;
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
        PublishBlockProof(header, announce: false);
        return true;
    }
}
