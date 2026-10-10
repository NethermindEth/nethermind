// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public sealed partial class LeanObjectTransport
{
    internal void OnAnnounce(LeanPeer peer, AnnounceObjectsMessage message)
    {
        bool enabled = IsEnabled;
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            long now = _clock.GetTimestamp();
            foreach (LeanDescriptor descriptor in message.Descriptors)
            {
                Interlocked.Increment(ref _stats.AnnouncedIn);
                LeanMetrics.Record(descriptor.Kind, LeanEvent.AnnouncementReceived);
                peer.Known.Add(descriptor.ObjectId);
                // A block proof is only fetched on demand during block import; its announcement is an availability hint.
                if (descriptor.Kind == LeanProtocol.KindBlockProof)
                {
                    AddHint(descriptor.BlockHash, peer);
                    continue;
                }
                ValueHash256 objectId = descriptor.ObjectId;
                if (!enabled || descriptor.ProfileId != LocalProfile || descriptor.ByteLength > LeanLimits.MaxObjectBytes
                    || _store.Contains(objectId) || _tombstones.Contains(objectId, now)) continue;
                if (_assemblies.TryGetValue(objectId, out LeanAssembly? existing))
                {
                    existing.AddSource(peer);
                    continue;
                }
                if (peer.Assemblies >= LeanLimits.MaxAssembliesPerPeer) continue;
                if (TryCreateAssembly(descriptor, null, peer, now) is { } assembly) assembly.Validate = true;
            }
            Schedule(outbox, now);
        }
        outbox.Flush();
    }

    private LeanAssembly? TryCreateAssembly(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, LeanPeer? origin, long now)
    {
        // A declared length never triggers an allocation of that length: only per-chunk bookkeeping is charged here.
        long metadata = descriptor.ChunkCount * 24L + 1024;
        if (_assemblies.Count >= LeanLimits.MaxAssemblies || metadata > LeanLimits.MaxIncompleteBytes - _incompleteBytes
            || (origin is not null && metadata > LeanLimits.MaxPeerBytes - origin.ChargedBytes)) return null;
        LeanAssembly assembly = new(descriptor, skeleton, now, metadata) { Origin = origin };
        _incompleteBytes += metadata;
        if (origin is not null)
        {
            origin.ChargedBytes += metadata;
            origin.Assemblies++;
            assembly.AddSource(origin);
        }
        _assemblies.Add(descriptor.ObjectId, assembly);
        Interlocked.Increment(ref _stats.Fetched);
        LeanMetrics.Record(descriptor.Kind, LeanEvent.FetchStarted);
        return assembly;
    }

    /// <summary>Spreads disjoint missing indices across an assembly's sources, one bounded request per source.</summary>
    private void Schedule(Outbox outbox, long now)
    {
        if (_disposed) return;
        // Block import waits on sidecars, so they are scheduled before gossip objects.
        foreach (bool waited in (ReadOnlySpan<bool>)[true, false])
        {
            foreach (LeanAssembly assembly in _assemblies.Values)
            {
                if ((assembly.Waiters.Count > 0) != waited || assembly.Dropped || assembly.Queued || assembly.IsComplete
                    || assembly.Sources.Count == 0) continue;
                if (!waited && _incompleteBytes > LeanLimits.MaxIncompleteBytes - LeanProtocol.MaxCreditBytes) return;
                ScheduleAssembly(assembly, outbox, now);
            }
        }
    }

    /// <summary>Issues chunk requests for an assembly's missing indices that are not already in flight.</summary>
    /// <remarks>
    /// Sources are visited round-robin, one request of at most <see cref="LeanProtocol.MaxChunksPerRequest"/> missing indices
    /// per source per round, until every missing index is in flight or no source has a free request slot.
    /// </remarks>
    private void ScheduleAssembly(LeanAssembly assembly, Outbox outbox, long now)
    {
        int next = 0;
        int sources = assembly.Sources.Count;
        bool issued = true;
        while (issued)
        {
            issued = false;
            int first = assembly.NextSource++;
            for (int s = 0; s < sources; s++)
            {
                LeanPeer source = assembly.Sources[(first + s) % sources];
                if (!source.CanRequest(now) || source.ChargedBytes > LeanLimits.MaxPeerBytes - LeanProtocol.MaxCreditBytes
                    || !source.Status.SupportsKind(assembly.Descriptor.Kind)) continue;
                List<int> indices = [];
                for (; next < assembly.Chunks.Length && indices.Count < LeanProtocol.MaxChunksPerRequest; next++)
                    if (assembly.Chunks[next] is null && assembly.InFlight[next] is null) indices.Add(next);
                if (indices.Count == 0) return;
                LeanChunksRequest request = new(++source.LastIssued, now, assembly, [.. indices]);
                foreach (int index in request.Indices) assembly.InFlight[index] = source;
                Issue(source, request);
                outbox.Send(source, new GetChunksMessage(request.Id, assembly.Descriptor.ObjectId, request.Indices));
                issued = true;
            }
        }
    }

    internal void OnChunk(LeanPeer peer, in LeanChunkView chunk)
    {
        Outbox outbox = new();
        LeanChunksRequest? request = null;
        int position = -1;
        lock (_gate)
        {
            if (peer.Closed) return;
            if (FindLive<LeanChunksRequest>(peer, chunk.RequestId, outbox) is { } live)
            {
                LeanDescriptor descriptor = live.Assembly.Descriptor;
                // Chunks arrive in request order, so each must be the next expected index; skipped or repeated ones are violations.
                position = live.Next;
                if (chunk.ObjectId != descriptor.ObjectId || position >= live.Indices.Length || live.Indices[position] != chunk.Index)
                    Violation(peer, $"chunk {chunk.Index} of {chunk.ObjectId.ToShortString()} is not the next requested index", outbox);
                else if (chunk.Data.Length != descriptor.ChunkLength(chunk.Index) || chunk.Branch.Length != descriptor.Depth)
                    Violation(peer, $"chunk {chunk.Index} geometry does not match {descriptor}", outbox);
                else if (live.Cancelled) Answer(live, position);
                else request = live;
            }
        }
        if (request is not null)
        {
            // Branch hashing runs outside the lock; the request is re-checked before anything is retained.
            bool valid = request.Assembly.Verifier.Verify(chunk.Index, chunk.Data, chunk.Branch);
            lock (_gate)
            {
                if (!peer.Closed && peer.Live.TryGetValue(chunk.RequestId, out LeanOutgoingRequest? current) && current == request)
                    AcceptChunk(peer, request, position, chunk, valid, outbox);
            }
        }
        outbox.Flush();
    }

    /// <summary>Consumes a request's credit for the next index of a cancelled request, whose payload is discarded unhashed.</summary>
    private static void Answer(LeanChunksRequest request, int position)
    {
        request.Next = position + 1;
        request.Answered[position] = true;
    }

    private void AcceptChunk(LeanPeer peer, LeanChunksRequest request, int position, in LeanChunkView chunk, bool valid, Outbox outbox)
    {
        LeanAssembly assembly = request.Assembly;
        if (!valid)
        {
            Violation(peer, $"bad branch for chunk {chunk.Index} of {assembly.Descriptor}", outbox);
            return;
        }
        Answer(request, position);
        if (request.Cancelled) return;
        int index = chunk.Index;
        if (assembly.InFlight[index] == peer) assembly.InFlight[index] = null;
        Interlocked.Increment(ref _stats.ChunksReceived);
        // A valid first response for a requested index advances the request, even when another peer already supplied the
        // chunk; only a newly verified missing chunk advances the assembly.
        long now = _clock.GetTimestamp();
        request.LastProgress = now;
        request.Progressed = true;
        peer.Stalls = 0;
        if (assembly.Dropped || assembly.Queued) return;
        if (assembly.Chunks[index] is { } existing)
        {
            // An identical duplicate is neither allocated again nor counted as assembly progress.
            if (!existing.AsSpan().SequenceEqual(chunk.Data)) Violation(peer, $"conflicting chunk {index} of {assembly.Descriptor}", outbox);
            return;
        }
        long charge = assembly.ChargeOf(index);
        if (charge > LeanLimits.MaxIncompleteBytes - _incompleteBytes || charge > LeanLimits.MaxPeerBytes - peer.ChargedBytes)
        {
            // Discarded under local pressure: answered for Complete's purposes, and requestable again.
            return;
        }
        _incompleteBytes += charge;
        peer.ChargedBytes += charge;
        assembly.Chunks[index] = chunk.Data.ToArray();
        assembly.ChargedTo[index] = peer;
        assembly.Received++;
        assembly.Updated = now;
        // A new chunk of a direct recovery object also advances the wrappers waiting on it.
        foreach (LeanAssembly dependent in assembly.Dependents)
            if (!dependent.Dropped) dependent.Updated = now;
        LeanMetrics.ChunkReceived();
        if (assembly.IsComplete) TryComplete(assembly, outbox);
    }

    internal void OnComplete(LeanPeer peer, CompleteMessage message)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            LeanChunksRequest? request = FindLive<LeanChunksRequest>(peer, message.RequestId, outbox);
            if (request is not null)
            {
                LeanAssembly assembly = request.Assembly;
                if (message.ObjectId != assembly.Descriptor.ObjectId) Violation(peer, "Complete names another object", outbox);
                else if (message.Status == LeanCompleteStatus.Served && Array.IndexOf(request.Answered, false) >= 0)
                    Violation(peer, "Served with chunks missing", outbox);
                else if (request.Cancelled)
                {
                    // Any terminal status releases a cancelled request's slot, including Served crossing Cancel.
                    peer.Live.Remove(request.Id);
                    Schedule(outbox, _clock.GetTimestamp());
                }
                else
                {
                    long now = _clock.GetTimestamp();
                    peer.Live.Remove(request.Id);
                    ReleaseInFlight(request, peer);
                    switch (message.Status)
                    {
                        case LeanCompleteStatus.Unavailable or LeanCompleteStatus.Unsupported or LeanCompleteStatus.TooLarge:
                            assembly.Sources.Remove(peer);
                            break;
                        case LeanCompleteStatus.Busy:
                            peer.ThrottledUntil = now + (long)(LeanLimits.BusyRetry.TotalSeconds * _clock.TimestampFrequency);
                            break;
                    }
                    Schedule(outbox, now);
                }
            }
        }
        outbox.Flush();
    }

    internal void OnObjects(LeanPeer peer, ObjectsMessage message)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            LeanObjectsRequest? request = FindLive<LeanObjectsRequest>(peer, message.RequestId, outbox);
            if (request is not null)
            {
                // After cancellation only the terminal structure is checked; returned descriptors are neither checked nor used.
                string? error = request.Cancelled
                    ? message.Results.Length != request.Selectors.Length ? "Objects results do not match selectors" : null
                    : CheckObjects(request.Selectors, message.Results);
                if (error is not null) Violation(peer, error, outbox);
                else
                {
                    peer.Live.Remove(request.Id);
                    request.Completion.TrySetResult(message.Results);
                }
            }
        }
        outbox.Flush();
    }

    private string? CheckObjects(LeanSelector[] selectors, LeanObjectResult[] results)
    {
        if (results.Length != selectors.Length) return "Objects results do not match selectors";
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i] is not { Status: LeanResultStatus.Ok, Descriptor: { } descriptor } result) continue;
            if (!selectors[i].Matches(descriptor)) return "descriptor does not match its selector";
            if (descriptor.ByteLength > LeanLimits.MaxObjectBytes) return "descriptor exceeds the advertised max_object_bytes";
            if (descriptor.Kind != LeanProtocol.KindBlockProof) continue;
            if (result.Skeleton is null) return "missing header skeleton";
            if (!result.Skeleton.TryValidate(descriptor, _headerDecoder, _specProvider, out string? skeletonError))
                return $"invalid header skeleton: {skeletonError}";
        }
        return null;
    }

    internal void OnTransactions(LeanPeer peer, TransactionsMessage message)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            LeanTransactionsRequest? request = FindLive<LeanTransactionsRequest>(peer, message.RequestId, outbox);
            if (request is not null)
            {
                string? error = message.Results.Length != request.Hashes.Length ? "Transactions results do not match hashes" : null;
                // After cancellation the envelopes are discarded without hashing.
                for (int i = 0; error is null && !request.Cancelled && i < message.Results.Length; i++)
                    if (message.Results[i].Status == LeanResultStatus.Ok && ValueKeccak.Compute(message.Results[i].Envelope) != request.Hashes[i])
                        error = "transaction envelope does not match its hash";
                if (error is not null) Violation(peer, error, outbox);
                else
                {
                    peer.Live.Remove(request.Id);
                    request.Completion.TrySetResult(message.Results);
                }
            }
        }
        outbox.Flush();
    }

    internal async Task<LeanObjectResult[]?> RequestObjectsAsync(LeanPeer peer, LeanSelector[] selectors, CancellationToken cancellationToken)
    {
        LeanObjectsRequest request;
        Outbox outbox = new();
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_disposed || !peer.CanRequest(now)) return null;
            request = new(++peer.LastIssued, now, selectors);
            Issue(peer, request);
            outbox.Send(peer, new GetObjectsMessage(request.Id, selectors));
        }
        outbox.Flush();
        return await AwaitResponse(peer, request, request.Completion.Task, cancellationToken);
    }

    internal async Task<(LeanResultStatus Status, byte[] Envelope)[]?> RequestTransactionsAsync(LeanPeer peer, ValueHash256[] hashes,
        CancellationToken cancellationToken)
    {
        LeanTransactionsRequest request;
        Outbox outbox = new();
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_disposed || !peer.CanRequest(now)) return null;
            request = new(++peer.LastIssued, now, hashes);
            Issue(peer, request);
            outbox.Send(peer, new GetTransactionsMessage(request.Id, hashes));
        }
        outbox.Flush();
        return await AwaitResponse(peer, request, request.Completion.Task, cancellationToken);
    }

    private async Task<T?> AwaitResponse<T>(LeanPeer peer, LeanOutgoingRequest request, Task<T?> response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Cancel(peer, request);
            return null;
        }
    }

    /// <summary>Sends Cancel and marks the request; it keeps its slot until a terminal response or its local expiry.</summary>
    private void Cancel(LeanPeer peer, LeanOutgoingRequest request)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed || !peer.Live.TryGetValue(request.Id, out LeanOutgoingRequest? live) || live != request) return;
            MarkCancelled(peer, request, outbox);
        }
        outbox.Flush();
    }

    private void MarkCancelled(LeanPeer peer, LeanOutgoingRequest request, Outbox outbox)
    {
        if (request.Cancelled) return;
        request.Cancelled = true;
        Fail(request, peer);
        outbox.Send(peer, new CancelMessage(request.Id));
    }

    private void Tick()
    {
        Outbox outbox = new();
        long now;
        lock (_gate)
        {
            if (_disposed) return;
            now = _clock.GetTimestamp();
            ExpireRequests(now, outbox);
            ExpireAssemblies(now, outbox);
            // TryComplete can remove an assembly that nobody waits for, so iterate a snapshot.
            foreach (LeanAssembly assembly in (LeanAssembly[])[.. _assemblies.Values])
                if (assembly.IsComplete && !assembly.Queued && !assembly.Dropped) TryComplete(assembly, outbox);
            Schedule(outbox, now);
            LeanMetrics.Gauges(_peers.Count, _store.Count, _assemblies.Count, _incompleteBytes);
        }
        outbox.Flush();
        LogStats(now);
    }

    /// <summary>Expires requests at their idle or absolute deadline; only a useful result refreshes the idle deadline.</summary>
    private void ExpireRequests(long now, Outbox outbox)
    {
        List<LeanOutgoingRequest>? expired = null;
        foreach (LeanPeer peer in _peers)
        {
            expired?.Clear();
            foreach (LeanOutgoingRequest request in peer.Live.Values)
                if (_clock.GetElapsedTime(request.LastProgress, now) >= LeanProtocol.MaxRequestIdle
                    || _clock.GetElapsedTime(request.Created, now) >= LeanProtocol.MaxRequestAge)
                    (expired ??= []).Add(request);
            if (expired is null) continue;
            foreach (LeanOutgoingRequest request in expired)
            {
                peer.Live.Remove(request.Id);
                LeanMetrics.Record(0, LeanEvent.RequestExpired);
                // A cancelled request already released its work and told the responder; expiry only frees its slot.
                if (request.Cancelled) continue;
                Fail(request, peer);
                if (request is LeanChunksRequest) outbox.Send(peer, new CancelMessage(request.Id));
                if (request.Progressed) continue;
                // Isolated stalls are tolerated; repeated stalls throttle the source for a while.
                if (++peer.Stalls >= LeanLimits.MaxStalledRequests)
                {
                    peer.Stalls = 0;
                    peer.ThrottledUntil = now + (long)(LeanLimits.StallThrottle.TotalSeconds * _clock.TimestampFrequency);
                    if (_logger.IsDebug) _logger.Debug($"lean/1 throttling {peer} after repeated stalled requests");
                }
            }
        }
    }

    /// <summary>Expires assemblies on a timer independent of incoming messages.</summary>
    /// <remarks>
    /// Only retrieval progress refreshes idleness: a newly verified missing chunk, a newly resolved missing transaction, or a
    /// new chunk of an object recovering one. Completed bodies awaiting validation or recovery keep their deadlines: expiry
    /// releases a body still queued and cancels the work of one being processed.
    /// </remarks>
    private void ExpireAssemblies(long now, Outbox outbox)
    {
        List<LeanAssembly>? expired = null;
        foreach (LeanAssembly assembly in _assemblies.Values)
            if (!assembly.Dropped && (_clock.GetElapsedTime(assembly.Updated, now) >= LeanProtocol.MaxAssemblyIdle
                || _clock.GetElapsedTime(assembly.Created, now) >= LeanProtocol.MaxAssemblyAge))
                (expired ??= []).Add(assembly);
        if (expired is null) return;
        foreach (LeanAssembly assembly in expired)
        {
            if (assembly.Started)
            {
                // The processing task releases the body and the assembly once its cancelled work unwinds.
                if (assembly.Processing is { IsCancellationRequested: false } processing) outbox.Cancel(processing);
                continue;
            }
            _assemblies.Remove(assembly.Descriptor.ObjectId);
            CancelRequestsFor(assembly, outbox);
            if (assembly.Queued) ReleaseCompletion(assembly);
            Drop(assembly);
            Interlocked.Increment(ref _stats.Expired);
            LeanMetrics.Record(assembly.Descriptor.Kind, LeanEvent.Expired);
            if (_logger.IsDebug) _logger.Debug($"lean/1 assembly expired with {assembly.Received}/{assembly.Chunks.Length} chunks: {assembly.Descriptor}");
        }
    }

    private void CancelRequestsFor(LeanAssembly assembly, Outbox outbox)
    {
        List<LeanChunksRequest>? cancelled = null;
        foreach (LeanPeer peer in _peers)
        {
            cancelled?.Clear();
            foreach (LeanOutgoingRequest request in peer.Live.Values)
                if (request is LeanChunksRequest { Cancelled: false } chunks && chunks.Assembly == assembly) (cancelled ??= []).Add(chunks);
            if (cancelled is null) continue;
            foreach (LeanChunksRequest request in cancelled) MarkCancelled(peer, request, outbox);
        }
    }

    /// <summary>Releases every byte charged for an assembly and fails its waiters; chunks from other sources go with it.</summary>
    private void Drop(LeanAssembly assembly)
    {
        if (assembly.Dropped) return;
        assembly.Dropped = true;
        ReleaseChunks(assembly);
        foreach (TaskCompletionSource<byte[]?> waiter in assembly.Waiters) waiter.TrySetResult(null);
        assembly.Waiters.Clear();
    }

    private void ReleaseChunks(LeanAssembly assembly)
    {
        for (int index = 0; index < assembly.Chunks.Length; index++)
        {
            if (assembly.ChargedTo[index] is { } peer)
            {
                long charge = assembly.ChargeOf(index);
                peer.ChargedBytes -= charge;
                _incompleteBytes -= charge;
                assembly.ChargedTo[index] = null;
            }
            assembly.Chunks[index] = null;
        }
        if (assembly.MetadataReleased) return;
        assembly.MetadataReleased = true;
        _incompleteBytes -= assembly.MetadataBytes;
        if (assembly.Origin is { } origin)
        {
            origin.ChargedBytes -= assembly.MetadataBytes;
            origin.Assemblies--;
        }
    }

    private void AddHint(in ValueHash256 blockHash, LeanPeer peer)
    {
        if (!_hints.TryGetValue(blockHash, out List<LeanPeer>? peers))
        {
            if (_hintOrder.Count >= LeanLimits.MaxAvailabilityHints && _hintOrder.TryDequeue(out ValueHash256 oldest)) _hints.Remove(oldest);
            _hints[blockHash] = peers = [];
            _hintOrder.Enqueue(blockHash);
        }
        if (!peers.Contains(peer) && peers.Count < LeanLimits.MaxSourcesPerAssembly) peers.Add(peer);
        TaskCompletionSource signal = _hintSignal;
        _hintSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
    }
}
