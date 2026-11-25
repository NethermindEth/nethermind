// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V75.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V75;

public sealed partial class ProofWrapperBroadcaster
{
    private const int MaxBusyRetries = 50;

    /// <summary>The outcome of validating a received wrapper.</summary>
    /// <remarks>Only <see cref="Malformed"/> penalizes the serving peer. <see cref="Invalid"/> is a proof or dependency-rule
    /// failure, discarded without penalty (EIP-8437); <see cref="LocalFailure"/> is a local inability to finish validation.</remarks>
    private enum Verdict { Valid, PoolRejected, Malformed, Invalid, LocalFailure }

    // ---- Announcements and wrapper requests ---------------------------------------------------------------------------

    /// <summary>Records announced wrappers this node wants; announcements establish no validity and require no action.</summary>
    /// <remarks>A wrapper whose coverage hints, where announced, match a held wrapper validated under the current rules is not
    /// fetched, nor one whose metadata lists only transactions it adds no coverage for. Claims found false against a held wrapper
    /// or transaction are discarded with their sender (EIP-8437).</remarks>
    internal void OnAnnounce(WrapperPeer peer, NewProofWrapperHashesMessage message)
    {
        bool enabled = IsEnabled;
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            long now = _clock.GetTimestamp();
            // Announcements beyond the rate limit are ignored, not penalized.
            if (!peer.AnnouncementsIn.TryTake(1)) return;
            bool evictable = true;
            int share = Math.Max(WrapperTransportLimits.MinPendingAnnouncementsPerPeer, WrapperTransportLimits.MaxPendingAnnouncements / _peers.Count);
            foreach (WrapperAnnouncement announcement in message.Announcements)
            {
                WrapperMetrics.Record(WrapperEvent.AnnouncementReceived);
                ValueHash256 wrapperHash = announcement.Hash;
                peer.Known.Add(wrapperHash);
                peer.Exchanged.Add(wrapperHash);
                if (!enabled) continue;
                if (FalseAgainstHeld(announcement) is { } falseClaim)
                {
                    Violation(peer, falseClaim, outbox);
                    break;
                }
                if (_store.Contains(wrapperHash) || IsTombstoned(wrapperHash, now)) continue;
                if (_fetches.TryGetValue(wrapperHash, out WrapperFetch? existing))
                {
                    existing.AddSource(peer, announcement);
                    continue;
                }
                if (announcement.Coverage is { } coverage && _store.HoldsEquivalent(coverage, announcement.Mode, _verifier))
                {
                    WrapperMetrics.Record(WrapperEvent.EquivalentSkipped);
                    continue;
                }
                if (announcement.Mode == MempoolWrapper.ModeDirect && announcement.Metadata is { } metadata && AddsNoCoverage(metadata))
                {
                    WrapperMetrics.Record(WrapperEvent.HeldSkipped);
                    continue;
                }
                // The pending cache is bounded, and each peer holds at most an equal share of it, so peers announcing wrappers
                // they never serve cannot crowd out the others. Announcements are not repeated, so excess ones are lost.
                if (peer.PendingFetches >= share) continue;
                if (_fetches.Count >= WrapperTransportLimits.MaxPendingAnnouncements && (!evictable || !EvictPending(peer, share)))
                {
                    // Nothing becomes evictable while the rest of the message is handled.
                    evictable = false;
                    continue;
                }
                WrapperFetch fetch = new(announcement, now) { Announcer = peer, WantsManifest = WantsManifest(peer, announcement) };
                peer.PendingFetches++;
                fetch.AddSource(peer, announcement);
                _fetches.Add(wrapperHash, fetch);
            }
            Schedule(outbox);
        }
        outbox.Flush();
    }

    /// <summary>Why <paramref name="announcement"/> is false against a held wrapper of its hash or, for its metadata, against held
    /// transactions; null when nothing held contradicts it.</summary>
    /// <remarks>A held transaction is one carried by a held, validated wrapper, whose canonical hash binds its metadata. Its pooled
    /// size is compared only against the wrapper of the announced hash, since a consensus hash does not bind it.</remarks>
    private string? FalseAgainstHeld(WrapperAnnouncement announcement)
    {
        if (_store.TryGet(announcement.Hash, out WrapperStore.Entry held))
        {
            WrapperAnnouncement truth = held.Announcement;
            return announcement.Mode != truth.Mode || announcement.Length != truth.Length || !Equals(announcement.Metadata, truth.Metadata)
                || announcement.Coverage is { } coverage && coverage != truth.Coverage
                ? $"announced false claims for held {truth}" : null;
        }
        return announcement.Metadata is { } metadata ? FalseAgainstHeld(metadata) : null;
    }

    /// <summary>Why <paramref name="metadata"/> is false against a transaction held in a validated wrapper, or null.</summary>
    private string? FalseAgainstHeld(WrapperMetadata metadata)
    {
        for (int i = 0; i < metadata.Count; i++)
        {
            ValueHash256 transaction = metadata.Hashes[i];
            IReadOnlyList<ValueHash256> holders = _store.Listing(transaction);
            if (holders.Count == 0 || !_store.TryGet(holders[0], out WrapperStore.Entry entry)) continue;
            int index = Array.BinarySearch(entry.Hashes, transaction);
            if (index >= 0 && !metadata.DescribesAlike(i, entry.Manifest, index))
                return $"announced false metadata for held transaction {transaction.ToShortString()}";
        }
        return null;
    }

    /// <summary>Whether a mode-0 wrapper whose metadata is <paramref name="metadata"/> would add no coverage: every transaction it
    /// lists is pending and, while an <c>eth/75</c> peer is connected, carried by a held mode-0 wrapper that peer can be served.</summary>
    /// <remarks>EIP-8437 lets requesters use metadata to choose fetches; it establishes no coverage, so this only skips a fetch.</remarks>
    private bool AddsNoCoverage(WrapperMetadata metadata)
    {
        if (!_wrappers.AreAllPending(metadata.Hashes)) return false;
        if (_peers.TrueForAll(static peer => peer.Aggregates)) return true;
        foreach (ValueHash256 transaction in metadata.Hashes)
            if (!HeldDirectly(transaction)) return false;
        return true;
    }

    private bool HeldDirectly(in ValueHash256 transaction)
    {
        foreach (ValueHash256 wrapperHash in _store.Listing(transaction))
            if (TryGetOffered(wrapperHash, out WrapperStore.Entry entry) && entry.Shape.Mode == MempoolWrapper.ModeDirect) return true;
        return false;
    }

    /// <summary>Whether one held mode-1 wrapper offered under the current rules carries every one of <paramref name="transactions"/>,
    /// so a mode-1 wrapper of them adds no reusable proof.</summary>
    private bool HeldRecursively(ValueHash256[] transactions)
    {
        foreach (ValueHash256 wrapperHash in _store.Listing(transactions[0]))
        {
            if (!TryGetOffered(wrapperHash, out WrapperStore.Entry entry) || entry.Shape.Mode != MempoolWrapper.ModeRecursive) continue;
            bool covers = true;
            foreach (ValueHash256 transaction in transactions) covers &= Array.BinarySearch(entry.Hashes, transaction) >= 0;
            if (covers) return true;
        }
        return false;
    }

    /// <summary>Whether a mode-1 wrapper announced by <paramref name="peer"/> is fetched only once its <c>eth/76</c> manifest
    /// shows it adds coverage.</summary>
    /// <remarks>The manifest costs a round trip, so it is requested only while a held mode-1 wrapper could make the announced one
    /// redundant.</remarks>
    private bool WantsManifest(WrapperPeer peer, WrapperAnnouncement announcement) =>
        peer.Aggregates && announcement.Mode == MempoolWrapper.ModeRecursive && _store.RecursiveCount > 0;

    /// <summary>Makes room in the full pending cache for <paramref name="announcer"/> by dropping an unrequested wrapper every open
    /// source of which is deprioritized or holds at least its share.</summary>
    /// <returns>Whether room was made; never for an announcer that is itself deprioritized.</returns>
    private bool EvictPending(WrapperPeer announcer, int share)
    {
        if (announcer.IsDeprioritized) return false;
        foreach (WrapperFetch fetch in _fetches.Values)
        {
            if (fetch.Requested || fetch.Wrapper is not null || fetch.Announcer is not { } holder || !Yields(holder, share)) continue;
            bool evictable = true;
            foreach (WrapperPeer source in fetch.Sources) evictable &= Yields(source, share);
            if (!evictable) continue;
            Remove(fetch, "evicted for another peer's announcement");
            return true;
        }
        return false;

        static bool Yields(WrapperPeer peer, int share) => peer.Closed || peer.IsDeprioritized || peer.PendingFetches >= share;
    }

    /// <summary>Requests queued wrappers from their next untried source, up to four outstanding requests per peer.</summary>
    /// <remarks>One pass over the pending fetches groups them by next source, so the cost does not grow with the peer count.</remarks>
    private void Schedule(Outbox outbox)
    {
        if (_disposed || _peers.Count == 0) return;
        long now = _clock.GetTimestamp();
        List<WrapperFetch>? exhausted = null;
        Dictionary<WrapperPeer, List<WrapperFetch>>? bySource = null;
        foreach (WrapperFetch fetch in _fetches.Values)
        {
            if (fetch.Requested || fetch.Wrapper is not null) continue;
            if (fetch.NextSource() is not { } source) (exhausted ??= []).Add(fetch);
            else if (source.CanRequest)
            {
                // Only an eth/76 source serves manifests.
                fetch.WantsManifest &= source.Aggregates;
                bySource ??= [];
                if (!bySource.TryGetValue(source, out List<WrapperFetch>? queued)) bySource[source] = queued = [];
                queued.Add(fetch);
            }
        }
        foreach (WrapperFetch fetch in exhausted ?? []) Remove(fetch, "every source tried");
        if (bySource is null) return;
        int count = _peers.Count;
        int first = _nextPeer % count;
        for (int p = 0; p < count; p++)
        {
            WrapperPeer peer = _peers[(first + p) % count];
            if (!bySource.TryGetValue(peer, out List<WrapperFetch>? queued)) continue;
            foreach (bool manifests in (ReadOnlySpan<bool>)[true, false])
            {
                List<WrapperFetch> wanted = queued.FindAll(fetch => fetch.WantsManifest == manifests);
                int next = 0;
                while (next < wanted.Count && peer.CanRequest && RequestFrom(peer, wanted, manifests, ref next, now, outbox))
                    _nextPeer = (first + p + 1) % count;
            }
        }
    }

    /// <summary>Sends one GetProofWrappers, or with <paramref name="manifests"/> one GetProofWrapperManifests, for the next queued
    /// wrappers of <paramref name="peer"/>, within the soft limit.</summary>
    /// <remarks>A manifest describes transactions in fewer bytes than the wrapper carries them, so the wrappers' announced lengths
    /// bound either response.</remarks>
    private bool RequestFrom(WrapperPeer peer, List<WrapperFetch> queued, bool manifests, ref int next, long now, Outbox outbox)
    {
        List<WrapperFetch> batch = [];
        long bytes = 0;
        for (; next < queued.Count && batch.Count < Eth75Protocol.MaxWrappersPerRequest; next++)
        {
            WrapperFetch fetch = queued[next];
            if (fetch.Requested || fetch.Wrapper is not null) continue;
            int length = EntryLength(fetch.LengthFrom(peer));
            if (batch.Count > 0 && bytes + length > Eth75Protocol.SoftResponseLimit) break;
            batch.Add(fetch);
            bytes += length;
        }
        // Requests are batched within the soft limit, so a response carries at most the requested bytes.
        long reserved = Math.Min(bytes + Eth75Protocol.ResponseFramingBytes, Eth75Protocol.LocalMaxResponseBytes);
        if (batch.Count == 0 || peer.LastIssued == long.MaxValue || !TryCharge(peer, reserved)) return false;
        ValueHash256[] ids = new ValueHash256[batch.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            WrapperFetch fetch = batch[i];
            fetch.Requested = true;
            if (!manifests) fetch.Tried.Add(peer);
            fetch.Started ??= now;
            ids[i] = fetch.Announcement.Hash;
            peer.Known.Add(ids[i]);
        }
        if (manifests)
        {
            WrappersRequest manifestRequest = new(++peer.LastIssued, now, reserved, [.. batch], false, true);
            peer.Requests.Add(manifestRequest.Id, manifestRequest);
            outbox.Send(peer, new GetProofWrapperManifestsMessage(manifestRequest.Id, ids));
            WrapperMetrics.Record(WrapperEvent.ManifestRequested, ids.Length);
            return true;
        }
        // References are useful only once this node holds encodings the peer sent in full; a wrapper whose compact form
        // referenced an evicted encoding is fetched complete.
        bool compact = peer.Aggregates && !peer.Received.IsEmpty && batch.TrueForAll(static fetch => !fetch.RequiresComplete);
        WrappersRequest request = new(++peer.LastIssued, now, reserved, [.. batch], compact);
        peer.Requests.Add(request.Id, request);
        outbox.Send(peer, new GetProofWrappersMessage(request.Id, compact, ids));
        WrapperMetrics.Record(WrapperEvent.Requested, ids.Length);
        return true;
    }

    /// <summary>Matches returned entries, in order, to distinct requested hashes, resolves hash references from encodings
    /// cached from this peer, checks each wrapper's canonical hash and the responding peer's announced mode, length, metadata and
    /// coverage hints, then queues the wrappers for validation.</summary>
    /// <remarks>Requested wrappers skipped before the last one returned were unavailable from this peer; those after it were cut
    /// by the soft limit and are requested again from the same peer. A compact item referencing an encoding this node no longer
    /// holds is discarded without penalty and requested again with <c>compact = 0</c> (EIP-8437).</remarks>
    internal void OnProofWrappers(WrapperPeer peer, ProofWrappersMessage message)
    {
        ProofWrapperEntry[] entries = message.Entries;
        Outbox outbox = new();
        bool outstanding;
        lock (_gate)
        {
            if (peer.Closed) return;
            // Resolution parses and hashes every body, so a response to no outstanding request is settled before it.
            outstanding = peer.Requests.ContainsKey(message.RequestId);
            if (!outstanding) TakeRequest(peer, message.RequestId, outbox);
        }
        if (!outstanding)
        {
            outbox.Flush();
            return;
        }
        Returned[] returned = Resolve(peer, message);
        lock (_gate)
        {
            if (peer.Closed) return;
            WrappersRequest? request = TakeRequest(peer, message.RequestId, outbox);
            if (request is null || request.Manifests)
            {
                if (request is not null) MismatchedResponse(peer, request, outbox);
                outbox.Flush();
                return;
            }
            Release(peer, request.Reserved);
            WrapperFetch[] fetches = request.Fetches;
            int[] matched = new int[entries.Length];
            int cursor = 0;
            string? violation = null;
            for (int i = 0; i < entries.Length && violation is null; i++)
            {
                while (cursor < fetches.Length && fetches[cursor].Announcement.Hash != entries[i].Hash) cursor++;
                Returned item = returned[i];
                if (cursor == fetches.Length) violation = "a returned wrapper is unrequested, repeated or reordered";
                else if (item.Referenced && !request.Compact) violation = "a compact = 0 response carries a hash reference";
                else if (item.Error is not null) violation = item.Error;
                else if (fetches[cursor].ModeFrom(peer) != item.Mode) violation = "a returned wrapper does not match its announced mode or length";
                else if (item.Wrapper is null) matched[i] = cursor++;
                else if (item.Hash != entries[i].Hash) violation = "a returned wrapper does not match its hash";
                else if (fetches[cursor].LengthFrom(peer) != item.Wrapper.Length)
                    violation = "a returned wrapper does not match its announced mode or length";
                // EIP-8437: false metadata and hints are penalized before, and independently of, proof verification.
                else if (item.Parsed && (!Equals(item.Metadata, fetches[cursor].ClaimFrom(peer).Metadata)
                    || fetches[cursor].ManifestFrom(peer) is { } manifest && !manifest.Equals(item.Manifest)))
                    violation = "a returned wrapper does not match its announced transaction metadata or manifest";
                else if (item.Coverage is { } coverage && fetches[cursor].CoverageFrom(peer) is { } announced && coverage != announced)
                    violation = "a returned wrapper does not match its announced dependency or transaction digest";
                else matched[i] = cursor++;
            }
            if (violation is not null)
            {
                foreach (WrapperFetch fetch in fetches) fetch.Requested = false;
                Violation(peer, violation, outbox);
                Schedule(outbox);
                outbox.Flush();
                return;
            }
            int last = entries.Length == 0 ? -1 : matched[^1];
            int next = 0, unavailable = 0;
            for (int position = 0; position < fetches.Length; position++)
            {
                WrapperFetch fetch = fetches[position];
                fetch.Requested = false;
                if (next < matched.Length && matched[next] == position)
                {
                    Returned item = returned[next++];
                    peer.Exchanged.Add(fetch.Announcement.Hash);
                    if (item.Wrapper is not { } wrapper)
                    {
                        // A cache miss: retry the same peer with a fresh request for the complete wrapper.
                        WrapperMetrics.Record(WrapperEvent.ReferenceMissed);
                        fetch.RequiresComplete = true;
                        fetch.Tried.Remove(peer);
                        continue;
                    }
                    if (fetch.Removed || fetch.Wrapper is not null) continue;
                    if (!TryCharge(peer, item.Charge))
                    {
                        WrapperMetrics.Record(WrapperEvent.BudgetDropped);
                        if (_logger.IsDebug) _logger.Debug($"eth/75 dropped {fetch.Announcement} from {peer}: byte budget exhausted");
                        continue;
                    }
                    fetch.Wrapper = wrapper;
                    fetch.ChargedBytes = item.Charge;
                    fetch.Server = peer;
                    // EIP-8437: other announcers' claims found false against the verified hash are discarded with their senders.
                    if (item.Parsed)
                        foreach (WrapperPeer claimant in fetch.FalseClaimants(peer, item.Mode, wrapper.Length, item.Metadata, item.Manifest!))
                            Violation(claimant, $"announced false mode, length or metadata for {fetch.Announcement}", outbox);
                    Enqueue(fetch);
                }
                // A wrapper after the last returned one was cut by the soft limit: ask the same peer again.
                else if (position > last && entries.Length > 0) fetch.Tried.Remove(peer);
                else if (entries.Length == 0) fetch.AnsweredEmpty.Add(peer);
                else unavailable++;
            }
            // Requested wrappers withheld count toward the failure score in proportion, so serving one of many does not evade it;
            // an empty response is a spent serving budget.
            if (unavailable > 0 && entries.Length > 0) peer.RecordFailure(WrapperTransportLimits.UnservedFailureWeight * unavailable / fetches.Length);
            WrapperMetrics.Record(WrapperEvent.Received, entries.Length);
            Schedule(outbox);
        }
        outbox.Flush();
    }

    /// <summary>Matches returned manifests, in order, to distinct requested hashes, checks each on arrival and against held
    /// transactions, and drops wanted wrappers a manifest shows add no coverage; the rest are fetched next (<c>eth/76</c>).</summary>
    /// <remarks>A manifest is <paramref name="peer"/>'s claim, checked against the wrapper if it is fetched from that peer. Hashes
    /// after the last one returned were cut by the soft limit and are asked again; a wrapper whose manifest was unavailable, or
    /// that an empty response or a timeout left without one, is fetched without it.</remarks>
    internal void OnProofWrapperManifests(WrapperPeer peer, ProofWrapperManifestsMessage message)
    {
        ProofWrapperManifest[] entries = message.Manifests;
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            WrappersRequest? request = TakeRequest(peer, message.RequestId, outbox);
            if (request is not null && !request.Manifests) MismatchedResponse(peer, request, outbox);
            else if (request is not null) MatchManifests(peer, request, entries, outbox);
        }
        outbox.Flush();
    }

    /// <summary>A ProofWrappers answering a manifest request, or the reverse: the request's wrappers go back to the queue and the
    /// peer is disconnected.</summary>
    private void MismatchedResponse(WrapperPeer peer, WrappersRequest request, Outbox outbox)
    {
        Abandon(peer, request);
        Violation(peer, $"response of the wrong type to request {request.Id}", outbox);
        Schedule(outbox);
    }

    private void MatchManifests(WrapperPeer peer, WrappersRequest request, ProofWrapperManifest[] entries, Outbox outbox)
    {
        Release(peer, request.Reserved);
        WrapperFetch[] fetches = request.Fetches;
        int[] matched = new int[entries.Length];
        int cursor = 0;
        string? violation = null;
        for (int i = 0; i < entries.Length && violation is null; i++)
        {
            while (cursor < fetches.Length && fetches[cursor].Announcement.Hash != entries[i].Hash) cursor++;
            violation = cursor == fetches.Length ? "a returned manifest is unrequested, repeated or reordered" : ManifestError(fetches[cursor], peer, entries[i].Manifest);
            matched[i] = cursor++;
        }
        if (violation is not null)
        {
            foreach (WrapperFetch fetch in fetches) fetch.Requested = false;
            Violation(peer, violation, outbox);
            Schedule(outbox);
            return;
        }
        int last = entries.Length == 0 ? -1 : matched[^1];
        int next = 0;
        long expiry = _clock.GetTimestamp() + (long)(WrapperTransportLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency);
        for (int position = 0; position < fetches.Length; position++)
        {
            WrapperFetch fetch = fetches[position];
            fetch.Requested = false;
            if (next < matched.Length && matched[next] == position)
            {
                WrapperMetadata manifest = entries[next++].Manifest;
                fetch.SetManifest(peer, manifest);
                fetch.WantsManifest = false;
                if (fetch.Removed || !HeldRecursively(manifest.Hashes)) continue;
                // Other announcers of the same aggregate are not asked for its manifest again for a while.
                _tombstones.Add(fetch.Announcement.Hash, expiry);
                Remove(fetch, "its manifest shows no new coverage");
                WrapperMetrics.Record(WrapperEvent.HeldSkipped);
            }
            else if (position <= last || entries.Length == 0) fetch.WantsManifest = false;
        }
        Schedule(outbox);
    }

    /// <summary>Why <paramref name="manifest"/>, returned by <paramref name="peer"/>, is malformed against its announcement or
    /// false against a held transaction, or null.</summary>
    private string? ManifestError(WrapperFetch fetch, WrapperPeer peer, WrapperMetadata manifest)
    {
        if (manifest.TransactionBytes >= fetch.LengthFrom(peer)) return "a returned manifest does not fit its announced length";
        if (fetch.CoverageFrom(peer) is { } coverage && manifest.TransactionsDigest != coverage.TxsHash)
            return "a returned manifest does not match its announced txs-hash";
        return FalseAgainstHeld(manifest);
    }

    /// <summary>A returned entry after reference resolution: the complete wrapper, its hash, coverage hints, inline and full
    /// metadata and admission charge, or no wrapper on a cache miss, or the reason it is malformed.</summary>
    /// <remarks>A wrapper that does not parse has no hints or metadata to check; validation then rejects it as malformed.</remarks>
    private readonly record struct Returned(int Mode, ValueHash256 Hash, byte[]? Wrapper, bool Referenced, string? Error, long Charge = 0,
        WrapperCoverage? Coverage = null, WrapperMetadata? Metadata = null, bool Parsed = false, WrapperMetadata? Manifest = null);

    private static Returned Complete(int mode, byte[] wrapper, bool referenced)
    {
        WrapperCoverage? coverage = null;
        WrapperMetadata? metadata = null, manifest = null;
        bool parsed = false;
        try
        {
            WrapperBodies.ParseWrapper(wrapper, out WrapperShape shape, out WrapperCoverage hints);
            coverage = hints;
            parsed = true;
            manifest = WrapperMetadata.Of(wrapper);
            if (shape.Mode == MempoolWrapper.ModeDirect && manifest.Count <= Eth75Protocol.MaxInlineTransactions) metadata = manifest;
        }
        catch (RlpException exception)
        {
            if (parsed) return new(mode, default, null, referenced, $"malformed wrapper transaction: {exception.Message}");
        }
        return new(mode, ValueKeccak.Compute(wrapper), wrapper, referenced, null, AdmissionCharge(wrapper), coverage, metadata, parsed, manifest);
    }

    /// <summary>What a held wrapper is charged to its server's byte budgets until validated: as ordinary transaction gossip, the
    /// larger of its bytes and a per-transaction overhead, so many small transactions sharing one proof cost their admission work;
    /// capped at two wrappers' worth, so any wrapper within <c>MAX_WRAPPER_BYTES</c> stays retrievable.</summary>
    private static long AdmissionCharge(byte[] wrapper) =>
        Math.Min(Math.Max(wrapper.Length, (long)WrapperCompaction.CountTransactions(wrapper) * InboundTransactionBudget.TransactionCharge),
            2L * Eth75Protocol.MaxWrapperBytes);

    /// <summary>Resolves and hashes the returned wrappers outside the lock, since a response carries up to 2 MiB.</summary>
    private Returned[] Resolve(WrapperPeer peer, ProofWrappersMessage message)
    {
        ProofWrapperEntry[] entries = message.Entries;
        Returned[] returned = new Returned[entries.Length];
        Dictionary<ValueHash256, int>? lengths = null;
        for (int i = 0; i < entries.Length; i++)
        {
            byte[] wire = entries[i].Wrapper;
            int mode = WrapperBodies.TryReadMode(wire);
            if (!WrapperCompaction.HasReferences(wire))
            {
                returned[i] = Complete(mode, wire, false);
                continue;
            }
            // Reconstruction is bounded by the length this peer announced; an entry matching no request fails matching later.
            lengths ??= AnnouncedLengths(peer, message.RequestId);
            if (!lengths.TryGetValue(entries[i].Hash, out int announced))
            {
                returned[i] = new(mode, default, wire, true, null);
                continue;
            }
            WrapperCompaction.Outcome outcome = WrapperCompaction.Reconstruct(wire, announced, hash => peer.Received.Get(hash),
                out byte[]? wrapper, out string? error);
            returned[i] = outcome switch
            {
                WrapperCompaction.Outcome.Complete => Complete(mode, wrapper!, true),
                WrapperCompaction.Outcome.Missing => new(mode, default, null, true, null),
                _ => new(mode, default, null, true, error)
            };
            if (outcome == WrapperCompaction.Outcome.Complete) WrapperMetrics.Record(WrapperEvent.ReferenceResolved);
        }
        return returned;
    }

    /// <summary>The lengths <paramref name="peer"/> announced for the wrappers of an outstanding request.</summary>
    private Dictionary<ValueHash256, int> AnnouncedLengths(WrapperPeer peer, long requestId)
    {
        Dictionary<ValueHash256, int> lengths = [];
        lock (_gate)
            if (peer.Requests.TryGetValue(requestId, out WrappersRequest? request))
                foreach (WrapperFetch fetch in request.Fetches) lengths[fetch.Announcement.Hash] = fetch.LengthFrom(peer);
        return lengths;
    }

    /// <summary>Drops pending announcements after their lifetime and cancels fetches past the fetch deadline.</summary>
    private void ExpireFetches(long now, Outbox outbox)
    {
        List<WrapperFetch>? expired = null;
        foreach (WrapperFetch fetch in _fetches.Values)
        {
            bool lapsed = fetch.Started is { } started
                ? _clock.GetElapsedTime(started, now) >= WrapperTransportLimits.FetchDeadline
                : _clock.GetElapsedTime(fetch.Announced, now) >= WrapperTransportLimits.PendingLifetime;
            if (lapsed) (expired ??= []).Add(fetch);
        }
        foreach (WrapperFetch fetch in expired ?? [])
        {
            WrapperMetrics.Record(WrapperEvent.Expired);
            // Validation releases its own fetch once its cancelled work unwinds.
            if (fetch.Processing is { } processing) outbox.Cancel(processing);
            else Remove(fetch, "deadline");
        }
    }

    private void Remove(WrapperFetch fetch, string reason)
    {
        if (_fetches.TryGetValue(fetch.Announcement.Hash, out WrapperFetch? current) && current == fetch)
        {
            _fetches.Remove(fetch.Announcement.Hash);
            if (fetch.Announcer is { } announcer) announcer.PendingFetches--;
        }
        Drop(fetch);
        if (_logger.IsTrace) _logger.Trace($"eth/75 stopped fetching {fetch.Announcement}: {reason}");
    }

    /// <summary>Releases every byte charged for a fetch.</summary>
    private void Drop(WrapperFetch fetch)
    {
        if (fetch.Removed) return;
        fetch.Removed = true;
        if (fetch.Server is { } server && fetch.ChargedBytes != 0) Release(server, fetch.ChargedBytes);
        fetch.ChargedBytes = 0;
        fetch.Wrapper = null;
    }

    // ---- Validation ---------------------------------------------------------------------------------------------------

    private void Enqueue(WrapperFetch fetch)
    {
        fetch.Processing = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        WrapperPeer server = fetch.Server!;
        if (!_validation.TryGetValue(server, out Queue<WrapperFetch>? queued))
        {
            _validation[server] = queued = [];
            // A dropped backlog may have left the server's turn queued.
            if (!_validationTurns.Contains(server)) _validationTurns.Enqueue(server);
        }
        queued.Enqueue(fetch);
        if (_validationWorkers < WrapperTransportLimits.MaxValidationWorkers)
        {
            _validationWorkers++;
            _ = Task.Run(ValidateQueueAsync);
        }
    }

    /// <summary>Takes the next received wrapper of the next server in turn.</summary>
    private bool TryDequeueValidation([NotNullWhen(true)] out WrapperFetch? fetch)
    {
        while (_validationTurns.TryDequeue(out WrapperPeer? server))
        {
            if (!_validation.TryGetValue(server, out Queue<WrapperFetch>? queued)) continue;
            fetch = queued.Dequeue();
            if (queued.Count == 0) _validation.Remove(server);
            else _validationTurns.Enqueue(server);
            return true;
        }
        fetch = null;
        return false;
    }

    /// <summary>Returns the wrappers a deprioritized peer served that await validation to their fetches, which ask the next
    /// source, so its backlog does not hold validation or byte budgets.</summary>
    private void DropBacklog(WrapperPeer server)
    {
        if (!server.IsDeprioritized || !_validation.Remove(server, out Queue<WrapperFetch>? queued)) return;
        foreach (WrapperFetch fetch in queued)
        {
            fetch.Processing?.Dispose();
            fetch.Processing = null;
            Release(server, fetch.ChargedBytes);
            fetch.ChargedBytes = 0;
            fetch.Wrapper = null;
            fetch.Server = null;
        }
    }

    private async Task ValidateQueueAsync()
    {
        try
        {
            while (true)
            {
                WrapperFetch? fetch;
                lock (_gate)
                    if (_disposed || !TryDequeueValidation(out fetch)) return;
                await ValidateAndReleaseAsync(fetch).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _validationWorkers--;
                if (_disposed)
                {
                    // Verifications may outlive Dispose; the last worker owns the gate's teardown.
                    if (_validationWorkers == 0) _verification.Dispose();
                }
                else if (_validation.Count != 0)
                {
                    _validationWorkers++;
                    _ = Task.Run(ValidateQueueAsync);
                }
            }
        }
    }

    private async Task ValidateAndReleaseAsync(WrapperFetch fetch)
    {
        CancellationTokenSource processing = fetch.Processing!;
        try
        {
            await ValidateAsync(fetch, processing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (processing.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"eth/75 validation of {fetch.Announcement} was cancelled");
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"eth/75 validation of {fetch.Announcement} failed: {exception.Message}");
        }
        finally
        {
            Outbox outbox = new();
            lock (_gate)
            {
                fetch.Processing = null;
                Remove(fetch, "validated");
                Schedule(outbox);
            }
            outbox.Flush();
            processing.Dispose();
        }
    }

    /// <summary>Checks the canonical encoding and fully validates the wrapper under EIP-8288.</summary>
    private async Task ValidateAsync(WrapperFetch fetch, CancellationToken cancellation)
    {
        byte[] wrapper = fetch.Wrapper!;
        try { WrapperBodies.ParseWrapper(wrapper, out _); }
        catch (RlpException exception)
        {
            Conclude(fetch, Verdict.Malformed, $"noncanonical wrapper: {exception.Message}");
            return;
        }
        (Verdict verdict, string? error, Eip8288Verifier? verifier) =
            await VerifyAsync(token => _wrappers.AcceptOwnedDetailedAsync(wrapper, token, fetch.Server?.Link), cancellation).ConfigureAwait(false);
        // Encodings received in full within a validated wrapper resolve the server's later references.
        if (verdict is Verdict.Valid or Verdict.PoolRejected && fetch.Server is { Closed: false } server)
            foreach ((ValueHash256 hash, byte[] encoding) in WrapperCompaction.Encodings(wrapper)) server.Received.Add(hash, encoding);
        Conclude(fetch, verdict, error, verifier);
    }

    /// <param name="verifier">For an invalid verdict, the verifier it was reached under; it suppresses fetching only while that
    /// verifier stays current.</param>
    private void Conclude(WrapperFetch fetch, Verdict verdict, string? error, Eip8288Verifier? verifier = null)
    {
        switch (verdict)
        {
            case Verdict.Valid:
                WrapperMetrics.Record(WrapperEvent.Validated);
                if (_logger.IsDebug) _logger.Debug($"eth/75 validated {fetch.Announcement}");
                break;
            // Pool policy is not a protocol matter and never penalizes: a valid proof stands even if transactions were refused, and
            // the wrapper is relayed unchanged under the service's partial-rejection rule. A soft tombstone keeps re-announcements of one that
            // is not relayed from fetching it again.
            case Verdict.PoolRejected:
                WrapperMetrics.Record(WrapperEvent.Validated);
                SoftTombstone(fetch);
                if (_logger.IsDebug) _logger.Debug($"eth/75 validated {fetch.Announcement}, but the pool rejected its transactions: {error}");
                break;
            case Verdict.Malformed:
                Reject(fetch, error ?? "malformed wrapper");
                break;
            // EIP-8437: a proof or dependency-rule failure discards the wrapper without penalizing the peer that served it.
            // A peer whose wrappers repeatedly fail is fetched from last and at a lower rate, never penalized for it.
            case Verdict.Invalid:
                WrapperMetrics.Record(WrapperEvent.Invalid);
                lock (_gate)
                {
                    if (verifier is { } judgedUnder) _invalid.Add(InvalidKey(fetch.Announcement.Hash, judgedUnder));
                    if (fetch.Server is { } server)
                    {
                        server.RecordFailure();
                        DropBacklog(server);
                    }
                }
                if (_logger.IsDebug) _logger.Debug($"eth/75 discarded {fetch.Announcement}: {error}");
                break;
            default:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                SoftTombstone(fetch);
                if (_logger.IsDebug) _logger.Debug($"eth/75 could not complete {fetch.Announcement}: {error}");
                break;
        }
    }

    private void SoftTombstone(WrapperFetch fetch)
    {
        lock (_gate) _tombstones.Add(fetch.Announcement.Hash, _clock.GetTimestamp()
            + (long)(WrapperTransportLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
    }

    /// <summary>Tombstones a malformed wrapper and penalizes the peer that served it.</summary>
    private void Reject(WrapperFetch fetch, string reason)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            _tombstones.Add(fetch.Announcement.Hash);
            if (fetch.Server is { Closed: false } server) Violation(server, $"served malformed {fetch.Announcement}: {reason}", outbox);
        }
        outbox.Flush();
        WrapperMetrics.Record(WrapperEvent.Malformed);
        if (_logger.IsDebug) _logger.Debug($"eth/75 rejected {fetch.Announcement}: {reason}");
    }

    private async Task<(Verdict, string?, Eip8288Verifier?)> VerifyAsync(Func<CancellationToken, Task<ProofWrapperAcceptance>> accept, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            ProofWrapperAcceptance result;
            // Proof verification and admission stay serialized across the bounded validation workers; a busy retry waits
            // outside, so other queued validations go first.
            await _verification.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                result = await accept(cancellation).ConfigureAwait(false);
            }
            finally { _verification.Release(); }
            switch (result.Status)
            {
                case ProofWrapperAcceptanceStatus.Accepted: return (Verdict.Valid, null, null);
                case ProofWrapperAcceptanceStatus.PoolRejected: return (Verdict.PoolRejected, result.Result.Error, null);
                case ProofWrapperAcceptanceStatus.Invalid: return (Verdict.Invalid, result.Result.Error, result.Verifier);
                case ProofWrapperAcceptanceStatus.Malformed: return (Verdict.Malformed, result.Result.Error, null);
                case ProofWrapperAcceptanceStatus.Busy or ProofWrapperAcceptanceStatus.Interrupted when attempt < MaxBusyRetries:
                    await Task.Delay(WrapperTransportLimits.BusyRetry, cancellation).ConfigureAwait(false);
                    continue;
                default: return (Verdict.LocalFailure, result.Result.Error, null);
            }
        }
    }
}
