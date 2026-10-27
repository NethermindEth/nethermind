// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

public sealed partial class ProofWrapperTransport
{
    private const int MaxBusyRetries = 50;

    /// <summary>The outcome of validating a received wrapper.</summary>
    /// <remarks>Only <see cref="Malformed"/> penalizes the serving peer. <see cref="Invalid"/> is a proof or dependency-rule
    /// failure, discarded without penalty (EIP-8437); <see cref="Refused"/> and <see cref="LocalFailure"/> are local inabilities
    /// to finish validation.</remarks>
    private enum Verdict { Valid, PoolRejected, Malformed, Invalid, LocalFailure, Refused }

    // ---- Announcements and wrapper requests ---------------------------------------------------------------------------

    /// <summary>Records announced wrappers this node wants; announcements establish no validity and require no action.</summary>
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
            foreach (WrapperAnnouncement announcement in message.Announcements)
            {
                WrapperMetrics.Record(WrapperEvent.AnnouncementReceived);
                ValueHash256 wrapperHash = announcement.Hash;
                peer.Known.Add(wrapperHash);
                peer.Exchanged.Add(wrapperHash);
                if (!enabled || _store.Contains(wrapperHash)
                    || IsTombstoned(wrapperHash, now)) continue;
                if (_fetches.TryGetValue(wrapperHash, out WrapperFetch? existing))
                {
                    existing.AddSource(peer, announcement.Mode, announcement.Length);
                    continue;
                }
                // The pending cache is bounded, and each peer holds at most a share of it, so one peer announcing wrappers it
                // never serves cannot crowd out the others. Announcements are not repeated, so excess ones are lost.
                if (_fetches.Count >= WrapperTransportLimits.MaxPendingAnnouncements
                    || peer.PendingFetches >= WrapperTransportLimits.MaxPendingAnnouncementsPerPeer) continue;
                WrapperFetch fetch = new(announcement, now) { Announcer = peer };
                peer.PendingFetches++;
                fetch.AddSource(peer, announcement.Mode, announcement.Length);
                _fetches.Add(wrapperHash, fetch);
            }
            Schedule(outbox);
        }
        outbox.Flush();
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
            int next = 0;
            while (next < queued.Count && peer.CanRequest && RequestFrom(peer, queued, ref next, now, outbox)) _nextPeer = (first + p + 1) % count;
        }
    }

    /// <summary>Sends one GetProofWrappers for the next queued wrappers of <paramref name="peer"/>, within the soft limit.</summary>
    private bool RequestFrom(WrapperPeer peer, List<WrapperFetch> queued, ref int next, long now, Outbox outbox)
    {
        List<WrapperFetch> batch = [];
        long bytes = 0;
        for (; next < queued.Count && batch.Count < Eth73Protocol.MaxWrappersPerRequest; next++)
        {
            WrapperFetch fetch = queued[next];
            if (fetch.Requested || fetch.Wrapper is not null) continue;
            int length = EntryLength(fetch.LengthFrom(peer));
            if (batch.Count > 0 && bytes + length > Eth73Protocol.SoftResponseLimit) break;
            batch.Add(fetch);
            bytes += length;
        }
        // Requests are batched within the soft limit, so a response carries at most the requested bytes.
        long reserved = Math.Min(bytes + Eth73Protocol.ResponseFramingBytes, Eth73Protocol.LocalMaxResponseBytes);
        if (batch.Count == 0 || peer.LastIssued == long.MaxValue || !TryCharge(peer, reserved)) return false;
        ValueHash256[] ids = new ValueHash256[batch.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            WrapperFetch fetch = batch[i];
            fetch.Requested = true;
            fetch.Tried.Add(peer);
            fetch.Started ??= now;
            ids[i] = fetch.Announcement.Hash;
            peer.Known.Add(ids[i]);
        }
        WrappersRequest request = new(++peer.LastIssued, now, reserved, [.. batch]);
        peer.Requests.Add(request.Id, request);
        // This client keeps no omission cache, so it always asks for complete wrappers.
        outbox.Send(peer, new GetProofWrappersMessage(request.Id, true, ids));
        WrapperMetrics.Record(WrapperEvent.Requested, ids.Length);
        return true;
    }

    /// <summary>Matches returned entries, in order, to distinct requested hashes, checks each wrapper's canonical hash and the
    /// responding peer's announced mode and length, then queues the wrappers for validation.</summary>
    /// <remarks>Requested wrappers skipped before the last one returned were unavailable from this peer; those after it were cut
    /// by the soft limit and are requested again from the same peer. Requests ask for complete wrappers, so an entry with a hash
    /// reference fails its hash check.</remarks>
    internal void OnProofWrappers(WrapperPeer peer, ProofWrappersMessage message)
    {
        ProofWrapperEntry[] entries = message.Entries;
        // Hash outside the lock: a response carries up to 2 MiB.
        (int Mode, ValueHash256 Hash)[] identities = new (int, ValueHash256)[entries.Length];
        for (int i = 0; i < identities.Length; i++)
            identities[i] = (WrapperBodies.TryReadMode(entries[i].Wrapper), ValueKeccak.Compute(entries[i].Wrapper));
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            WrappersRequest? request = TakeRequest(peer, message.RequestId, outbox);
            if (request is null)
            {
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
                if (cursor == fetches.Length) violation = "a returned wrapper is unrequested, repeated or reordered";
                else if (identities[i].Hash != entries[i].Hash) violation = "a returned wrapper does not match its hash";
                else if (fetches[cursor].ModeFrom(peer) != identities[i].Mode || fetches[cursor].LengthFrom(peer) != entries[i].Wrapper.Length)
                    violation = "a returned wrapper does not match its announced mode or length";
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
            int next = 0;
            for (int position = 0; position < fetches.Length; position++)
            {
                WrapperFetch fetch = fetches[position];
                fetch.Requested = false;
                if (next < matched.Length && matched[next] == position)
                {
                    int mode = identities[next].Mode;
                    byte[] wrapper = entries[next++].Wrapper;
                    peer.Exchanged.Add(fetch.Announcement.Hash);
                    if (fetch.Removed || fetch.Wrapper is not null || !TryCharge(peer, wrapper.Length)) continue;
                    fetch.Wrapper = wrapper;
                    fetch.ChargedBytes = wrapper.Length;
                    fetch.Server = peer;
                    fetch.AddSource(peer, mode, wrapper.Length);
                    Enqueue(fetch);
                }
                // A wrapper after the last returned one was cut by the soft limit: ask the same peer again.
                else if (position > last && entries.Length > 0) fetch.Tried.Remove(peer);
            }
            WrapperMetrics.Record(WrapperEvent.Received, entries.Length);
            Schedule(outbox);
        }
        outbox.Flush();
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
        if (_logger.IsTrace) _logger.Trace($"eth/73 stopped fetching {fetch.Announcement}: {reason}");
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
        _validation.Enqueue(fetch);
        if (_validationWorkers < WrapperTransportLimits.MaxValidationWorkers)
        {
            _validationWorkers++;
            _ = Task.Run(ValidateQueueAsync);
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
                    if (_disposed || !_validation.TryDequeue(out fetch)) return;
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
            if (_logger.IsDebug) _logger.Debug($"eth/73 validation of {fetch.Announcement} was cancelled");
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"eth/73 validation of {fetch.Announcement} failed: {exception.Message}");
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
            await VerifyAsync(token => _wrappers.AcceptOwnedDetailedAsync(wrapper, token), cancellation).ConfigureAwait(false);
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
                if (_logger.IsDebug) _logger.Debug($"eth/73 validated {fetch.Announcement}");
                break;
            // Pool policy is not a protocol matter: a valid proof stands even if its transactions were refused. The wrapper is
            // neither stored nor relayed, so a soft tombstone keeps re-announcements from fetching it again.
            case Verdict.PoolRejected:
                WrapperMetrics.Record(WrapperEvent.Validated);
                SoftTombstone(fetch);
                if (_logger.IsDebug) _logger.Debug($"eth/73 validated {fetch.Announcement}, but the pool rejected its transactions: {error}");
                break;
            case Verdict.Malformed:
                Reject(fetch, error ?? "malformed wrapper");
                break;
            // EIP-8437: a proof or dependency-rule failure discards the wrapper without penalizing the peer that served it.
            case Verdict.Invalid:
                WrapperMetrics.Record(WrapperEvent.Invalid);
                if (verifier is { } judgedUnder)
                    lock (_gate) _invalid.Add(InvalidKey(fetch.Announcement.Hash, judgedUnder));
                if (_logger.IsDebug) _logger.Debug($"eth/73 discarded {fetch.Announcement}: {error}");
                break;
            case Verdict.Refused:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"eth/73 refused {fetch.Announcement}: {error}");
                break;
            default:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                SoftTombstone(fetch);
                if (_logger.IsDebug) _logger.Debug($"eth/73 could not complete {fetch.Announcement}: {error}");
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
        if (_logger.IsDebug) _logger.Debug($"eth/73 rejected {fetch.Announcement}: {reason}");
    }

    private async Task<(Verdict, string?, Eip8288Verifier?)> VerifyAsync(Func<CancellationToken, Task<ProofWrapperAcceptance>> accept, CancellationToken cancellation)
    {
        // Proof verification and admission stay serialized across the bounded validation workers.
        await _verification.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                ProofWrapperAcceptance result = await accept(cancellation).ConfigureAwait(false);
                switch (result.Status)
                {
                    case ProofWrapperAcceptanceStatus.Accepted: return (Verdict.Valid, null, null);
                    case ProofWrapperAcceptanceStatus.PoolRejected: return (Verdict.PoolRejected, result.Result.Error, null);
                    case ProofWrapperAcceptanceStatus.Invalid: return (Verdict.Invalid, result.Result.Error, result.Verifier);
                    case ProofWrapperAcceptanceStatus.Malformed: return (Verdict.Malformed, result.Result.Error, null);
                    case ProofWrapperAcceptanceStatus.Refused: return (Verdict.Refused, result.Result.Error, null);
                    case ProofWrapperAcceptanceStatus.Busy when attempt < MaxBusyRetries:
                        await Task.Delay(WrapperTransportLimits.BusyRetry, cancellation).ConfigureAwait(false);
                        continue;
                    default: return (Verdict.LocalFailure, result.Result.Error, null);
                }
            }
        }
        finally { _verification.Release(); }
    }
}
