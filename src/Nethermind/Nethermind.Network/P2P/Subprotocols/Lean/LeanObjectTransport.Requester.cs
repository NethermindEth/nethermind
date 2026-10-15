// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
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
                if (peer.Assemblies < LeanLimits.MaxAssembliesPerPeer && TryCreateAssembly(descriptor, peer, now) is { } assembly)
                    assembly.Validate = true;
                // Senders suppress repeated announcements, so a wanted object refused by the limits waits for capacity to free.
                else Defer(peer, descriptor, now);
            }
            Schedule(outbox, now);
        }
        outbox.Flush();
    }

    /// <summary>Keeps an announcement refused by the assembly limits in the peer's bounded cache, dropping the oldest.</summary>
    private void Defer(LeanPeer peer, LeanDescriptor descriptor, long now)
    {
        List<(LeanDescriptor Descriptor, long Announced)> deferred = peer.Deferred;
        foreach ((LeanDescriptor pending, _) in deferred)
            if (pending.ObjectId == descriptor.ObjectId) return;
        if (deferred.Count >= LeanLimits.MaxDeferredPerPeer) deferred.RemoveAt(0);
        else _deferred++;
        deferred.Add((descriptor, now));
    }

    /// <summary>Starts or joins assemblies for deferred announcements as capacity frees.</summary>
    /// <remarks>Peers take freed capacity in turns, one assembly each per round, starting after the last peer served, and each
    /// peer's oldest entries first. An entry is dropped once its object is held, tombstoned or being assembled, or once it
    /// outlives <see cref="LeanLimits.DeferredLifetime"/>; an assembly's timers start only when it is created.</remarks>
    private void StartDeferred(long now)
    {
        bool created = true;
        while (created && _deferred > 0)
        {
            created = false;
            int count = _peers.Count;
            if (count == 0) return;
            int first = _deferredNext % count;
            for (int p = 0; p < count; p++)
            {
                if (!StartDeferred(_peers[(first + p) % count], now)) continue;
                created = true;
                _deferredNext = (first + p + 1) % count;
            }
        }
    }

    /// <summary>Drops lapsed entries, joins assemblies that exist and starts at most one, if capacity allows.</summary>
    private bool StartDeferred(LeanPeer peer, long now)
    {
        List<(LeanDescriptor Descriptor, long Announced)> deferred = peer.Deferred;
        bool created = false;
        int kept = 0;
        for (int i = 0; i < deferred.Count; i++)
        {
            (LeanDescriptor descriptor, long announced) = deferred[i];
            ValueHash256 objectId = descriptor.ObjectId;
            if (_store.Contains(objectId) || _tombstones.Contains(objectId, now)
                || _clock.GetElapsedTime(announced, now) >= LeanLimits.DeferredLifetime) continue;
            if (_assemblies.TryGetValue(objectId, out LeanAssembly? existing))
            {
                existing.AddSource(peer);
                continue;
            }
            // Creation is only tried while both assembly limits leave room; joining and lapsing need no capacity.
            if (!created && peer.Assemblies < LeanLimits.MaxAssembliesPerPeer && _assemblies.Count < LeanLimits.MaxAssemblies
                && TryCreateAssembly(descriptor, peer, now) is { } assembly)
            {
                assembly.Validate = true;
                created = true;
                continue;
            }
            deferred[kept++] = deferred[i];
        }
        _deferred -= deferred.Count - kept;
        deferred.RemoveRange(kept, deferred.Count - kept);
        return created;
    }

    private LeanAssembly? TryCreateAssembly(LeanDescriptor descriptor, LeanPeer? origin, long now, bool waited = false)
    {
        // A declared length never triggers an allocation of that length: only per-chunk bookkeeping is charged here.
        long metadata = descriptor.ChunkCount * 24L + 1024;
        if (_assemblies.Count >= LeanLimits.MaxAssemblies || !CanReserveObjectBytes(metadata, waited)
            || (origin is not null && metadata > LeanLimits.MaxPeerBytes - origin.ChargedBytes)) return null;
        LeanAssembly assembly = new(descriptor, now, metadata) { Origin = origin };
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
        StartDeferred(now);
        // An import waiting for a proof takes priority over gossip retrieval.
        foreach (bool waited in (ReadOnlySpan<bool>)[true, false])
        {
            foreach (LeanAssembly assembly in _assemblies.Values)
            {
                if ((assembly.Waiters.Count > 0) != waited || assembly.Dropped || assembly.Queued || assembly.IsComplete
                    || assembly.Sources.Count == 0) continue;
                if (!waited && assembly.RetrievalBytes == 0 && BufferedBytes > LeanLimits.MaxIncompleteBytes - LeanProtocol.MaxCreditBytes) continue;
                ScheduleAssembly(assembly, outbox, now);
            }
        }
    }

    private void ScheduleAssembly(LeanAssembly assembly, Outbox outbox, long now)
    {
        if (assembly.CurrentSource is { } failed && (failed.Closed || !assembly.Sources.Contains(failed)))
            RestartSource(assembly, failed, outbox);
        if (assembly.CurrentSource is null)
        {
            if (assembly.SourceAttempts >= LeanLimits.MaxSourcesPerAssembly) return;
            foreach (LeanPeer candidate in assembly.Sources)
            {
                if (!candidate.CanRequest(now) || !candidate.Status.SupportsKind(assembly.Descriptor.Kind)
                    || candidate.ChargedBytes > LeanLimits.MaxPeerBytes - LeanProtocol.MaxCreditBytes) continue;
                assembly.CurrentSource = candidate;
                assembly.SourceAttempts++;
                break;
            }
        }
        if (assembly.CurrentSource is not { } source) return;
        if (assembly.RetrievalBytes == 0)
        {
            long bodyBytes = (long)assembly.Descriptor.ByteLength;
            long workingBytes = assembly.Descriptor.Kind == LeanProtocol.KindWrapper
                ? Math.Min(bodyBytes, Eip8288Constants.MaxAggregateProofBytes
                    + (long)Eip8288Constants.MaxLeanSigDepsPerWrapper * Eip8288Constants.LeanSphincsWitnessBytes)
                : 0;
            long reserved = bodyBytes + workingBytes;
            // Gossip leaves room for a waited block proof as well as ingress frames and decode scratch.
            if (!CanReserveObjectBytes(reserved, assembly.Waiters.Count != 0)
                || reserved > LeanLimits.MaxPeerBytes - source.ChargedBytes - 4 * LeanProtocol.MaxMessageBytes) return;
            assembly.RetrievalBytes = reserved;
            _incompleteBytes += reserved;
            source.ChargedBytes += reserved;
        }
        while (source.CanRequest(now))
        {
            List<int> indices = [];
            for (int index = 0; index < assembly.Chunks.Length && indices.Count < LeanProtocol.MaxChunksPerRequest; index++)
                if (!assembly.Chunks[index] && assembly.InFlight[index] is null) indices.Add(index);
            if (indices.Count == 0 || source.LastIssued == ulong.MaxValue) return;
            if (!assembly.RetrievalStarted)
            {
                assembly.RetrievalStarted = true;
                assembly.Created = assembly.Updated = now;
            }
            LeanChunksRequest request = new(++source.LastIssued, now, assembly, [.. indices]);
            foreach (int index in request.Indices) assembly.InFlight[index] = source;
            Issue(source, request);
            outbox.Send(source, new GetChunksMessage(request.Id, assembly.Descriptor.ObjectId, request.Indices));
        }
    }

    private void RestartSource(LeanAssembly assembly, LeanPeer source, Outbox outbox)
    {
        CancelRequestsFor(assembly, outbox);
        ReleaseChunks(assembly, releaseMetadata: false);
        Array.Clear(assembly.InFlight);
        assembly.Received = 0;
        assembly.Sources.Remove(source);
        assembly.CurrentSource = null;
    }

    internal void OnChunk(LeanPeer peer, in LeanChunkView chunk)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            if (FindLive<LeanChunksRequest>(peer, chunk.RequestId, outbox) is { } live)
            {
                LeanDescriptor descriptor = live.Assembly.Descriptor;
                int position = live.Next;
                if (chunk.ObjectId != descriptor.ObjectId || position >= live.Indices.Length || live.Indices[position] != chunk.Index)
                    Violation(peer, "Chunk is not the next requested index", outbox);
                else if (chunk.Data.Length != descriptor.ChunkLength(chunk.Index))
                    Violation(peer, "Chunk geometry differs from the descriptor", outbox);
                else if (live.Cancelled) Answer(live, position);
                else if (live.Assembly.CurrentSource != peer) Violation(peer, "Chunk is not from the current source", outbox);
                else AcceptChunk(peer, live, position, chunk, outbox);
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

    private void AcceptChunk(LeanPeer peer, LeanChunksRequest request, int position, in LeanChunkView chunk, Outbox outbox)
    {
        LeanAssembly assembly = request.Assembly;
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
        int offset = index * LeanProtocol.ChunkBytes;
        if (assembly.Chunks[index])
        {
            if (!assembly.Body!.AsSpan(offset, chunk.Data.Length).SequenceEqual(chunk.Data))
                Violation(peer, $"Conflicting chunk {index} of {assembly.Descriptor}", outbox);
            return;
        }
        // Retrieval reserves body and decoder capacity; allocation waits for the first credited chunk.
        assembly.Body ??= new byte[assembly.Descriptor.ByteLength];
        chunk.Data.CopyTo(assembly.Body.AsSpan(offset));
        assembly.Chunks[index] = true;
        assembly.Received++;
        assembly.Updated = now;
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
                else if (!request.Cancelled && message.Status == LeanCompleteStatus.Served && Array.IndexOf(request.Answered, false) >= 0)
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
                            RestartSource(assembly, peer, outbox);
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

        }
        return null;
    }

    internal async Task<LeanObjectResult[]?> RequestObjectsAsync(LeanPeer peer, LeanSelector[] selectors, CancellationToken cancellationToken)
    {
        LeanObjectsRequest request;
        Outbox outbox = new();
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_disposed || !peer.CanRequest(now) || peer.LastIssued == ulong.MaxValue) return null;
            request = new(++peer.LastIssued, now, selectors);
            Issue(peer, request);
            outbox.Send(peer, new GetObjectsMessage(request.Id, selectors));
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
            if (!assembly.Dropped && (assembly.RetrievalStarted
                ? _clock.GetElapsedTime(assembly.Updated, now) >= LeanProtocol.MaxAssemblyIdle
                    || _clock.GetElapsedTime(assembly.Created, now) >= LeanProtocol.MaxAssemblyAge
                : _clock.GetElapsedTime(assembly.Admitted, now) >= LeanLimits.DeferredLifetime))
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
        foreach (TaskCompletionSource<LeanFetchedBody?> waiter in assembly.Waiters) waiter.TrySetResult(null);
        assembly.Waiters.Clear();
    }

    private void ReleaseChunks(LeanAssembly assembly, bool releaseMetadata = true)
    {
        if (!assembly.Queued && assembly.RetrievalBytes != 0)
        {
            long bytes = assembly.RetrievalBytes;
            _incompleteBytes -= bytes;
            if (assembly.CurrentSource is { } source) { source.ChargedBytes -= bytes; PruneNode(source.Node); }
            assembly.Body = null;
            assembly.RetrievalBytes = 0;
        }
        Array.Clear(assembly.Chunks);
        if (!releaseMetadata || assembly.MetadataReleased) return;
        assembly.MetadataReleased = true;
        _incompleteBytes -= assembly.MetadataBytes;
        if (assembly.Origin is { } origin)
        {
            origin.ChargedBytes -= assembly.MetadataBytes;
            origin.Assemblies--;
            PruneNode(origin.Node);
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
