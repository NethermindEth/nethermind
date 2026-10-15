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
                LeanMetrics.Record(LeanEvent.AnnouncementReceived);
                peer.Known.Add(descriptor.ObjectId);
                ValueHash256 objectId = descriptor.ObjectId;
                if (!enabled || descriptor.ProfileId != LocalProfile || descriptor.ByteLength > LeanLimits.MaxObjectBytes
                    || _store.Contains(objectId) || _tombstones.Contains(objectId, now)) continue;
                if (_assemblies.TryGetValue(objectId, out LeanAssembly? existing))
                {
                    existing.AddSource(peer);
                    continue;
                }
                // Senders suppress repeated announcements, so a wanted object refused by the limits waits for capacity to free.
                if (peer.Assemblies >= LeanLimits.MaxAssembliesPerPeer || TryCreateAssembly(descriptor, peer, now) is null)
                    Defer(peer, descriptor, now);
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
                && TryCreateAssembly(descriptor, peer, now) is not null)
            {
                created = true;
                continue;
            }
            deferred[kept++] = deferred[i];
        }
        _deferred -= deferred.Count - kept;
        deferred.RemoveRange(kept, deferred.Count - kept);
        return created;
    }

    private LeanAssembly? TryCreateAssembly(LeanDescriptor descriptor, LeanPeer origin, long now)
    {
        // A declared length never triggers an allocation of that length: only per-chunk bookkeeping is charged here.
        long metadata = descriptor.ChunkCount * 24L + 1024;
        if (_assemblies.Count >= LeanLimits.MaxAssemblies || !CanReserveObjectBytes(metadata)
            || metadata > LeanLimits.MaxPeerBytes - origin.ChargedBytes) return null;
        LeanAssembly assembly = new(descriptor, now, metadata) { Origin = origin };
        _incompleteBytes += metadata;
        origin.ChargedBytes += metadata;
        origin.Assemblies++;
        assembly.AddSource(origin);
        _assemblies.Add(descriptor.ObjectId, assembly);
        Interlocked.Increment(ref _stats.Fetched);
        LeanMetrics.Record(LeanEvent.FetchStarted);
        return assembly;
    }

    /// <summary>Spreads disjoint missing indices across an assembly's sources, one bounded request per source.</summary>
    private void Schedule(Outbox outbox, long now)
    {
        if (_disposed) return;
        StartDeferred(now);
        foreach (LeanAssembly assembly in _assemblies.Values)
        {
            if (assembly.Dropped || assembly.Queued || assembly.IsComplete || assembly.Sources.Count == 0) continue;
            if (assembly.RetrievalBytes == 0 && BufferedBytes > LeanLimits.MaxIncompleteBytes - LeanProtocol.MaxCreditBytes) continue;
            ScheduleAssembly(assembly, outbox, now);
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
                if (!candidate.CanRequest(now) || candidate.ChargedBytes > LeanLimits.MaxPeerBytes - LeanProtocol.MaxCreditBytes) continue;
                assembly.CurrentSource = candidate;
                assembly.SourceAttempts++;
                break;
            }
        }
        if (assembly.CurrentSource is not { } source) return;
        if (assembly.RetrievalBytes == 0)
        {
            long bodyBytes = (long)assembly.Descriptor.ByteLength;
            // Proof decoding may copy up to the largest verification input the wrapper can carry.
            long workingBytes = Math.Min(bodyBytes, Eip8288Constants.MaxAggregateProofBytes
                + (long)Eip8288Constants.MaxLeanSigDepsPerWrapper * Eip8288Constants.LeanSphincsWitnessBytes);
            long reserved = bodyBytes + workingBytes;
            // Retrieval leaves room for ingress frames and decode scratch.
            if (!CanReserveObjectBytes(reserved)
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
            source.Live.Add(request.Id, request);
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
            if (FindLive(peer, chunk.RequestId, outbox) is { } live)
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
            LeanChunksRequest? request = FindLive(peer, message.RequestId, outbox);
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

    /// <summary>Sends Cancel and marks the request; it keeps its slot until a terminal response or its local expiry.</summary>
    private static void MarkCancelled(LeanPeer peer, LeanChunksRequest request, Outbox outbox)
    {
        if (request.Cancelled) return;
        request.Cancelled = true;
        ReleaseInFlight(request, peer);
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
        List<LeanChunksRequest>? expired = null;
        foreach (LeanPeer peer in _peers)
        {
            expired?.Clear();
            foreach (LeanChunksRequest request in peer.Live.Values)
                if (_clock.GetElapsedTime(request.LastProgress, now) >= LeanLimits.RequestIdle
                    || _clock.GetElapsedTime(request.Created, now) >= LeanLimits.RequestAge)
                    (expired ??= []).Add(request);
            if (expired is null) continue;
            foreach (LeanChunksRequest request in expired)
            {
                peer.Live.Remove(request.Id);
                LeanMetrics.Record(LeanEvent.RequestExpired);
                // A cancelled request already released its work and told the responder; expiry only frees its slot.
                if (request.Cancelled) continue;
                ReleaseInFlight(request, peer);
                outbox.Send(peer, new CancelMessage(request.Id));
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
                ? _clock.GetElapsedTime(assembly.Updated, now) >= LeanLimits.AssemblyIdle
                    || _clock.GetElapsedTime(assembly.Created, now) >= LeanLimits.AssemblyAge
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
            LeanMetrics.Record(LeanEvent.Expired);
            if (_logger.IsDebug) _logger.Debug($"lean/1 assembly expired with {assembly.Received}/{assembly.Chunks.Length} chunks: {assembly.Descriptor}");
        }
    }

    private void CancelRequestsFor(LeanAssembly assembly, Outbox outbox)
    {
        List<LeanChunksRequest>? cancelled = null;
        foreach (LeanPeer peer in _peers)
        {
            cancelled?.Clear();
            foreach (LeanChunksRequest request in peer.Live.Values)
                if (!request.Cancelled && request.Assembly == assembly) (cancelled ??= []).Add(request);
            if (cancelled is null) continue;
            foreach (LeanChunksRequest request in cancelled) MarkCancelled(peer, request, outbox);
        }
    }

    /// <summary>Releases every byte charged for an assembly.</summary>
    private void Drop(LeanAssembly assembly)
    {
        if (assembly.Dropped) return;
        assembly.Dropped = true;
        ReleaseChunks(assembly);
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
}
