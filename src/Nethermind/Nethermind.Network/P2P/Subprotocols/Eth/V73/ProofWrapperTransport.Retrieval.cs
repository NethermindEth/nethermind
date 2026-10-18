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
    private const string ExhaustedReason = "every source tried";

    /// <summary>The outcome of validating a received wrapper.</summary>
    /// <remarks><see cref="Refused"/> and <see cref="LocalFailure"/> are local inabilities to finish validation and
    /// <see cref="Unresolved"/> a failed envelope recovery; none establishes invalidity or penalizes a peer.</remarks>
    private enum Verdict { Valid, Invalid, LocalFailure, Refused, Unresolved }

    // ---- Announcements and wrapper requests ---------------------------------------------------------------------------

    /// <summary>Records announced wrappers this node wants; announcements establish no validity and require no action.</summary>
    internal void OnAnnounce(WrapperPeer peer, NewProofWrapperHashesMessage message)
    {
        bool enabled = IsEnabled;
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            // Announcements beyond the rate limit are ignored, not penalized.
            if (!peer.AnnouncementsIn.TryTake(1))
            {
                Record(ProofWrapperEventTypes.AnnouncementsIgnored, peer, message: "NewProofWrapperHashes", count: message.Announcements.Length,
                    result: "announcement rate limit");
                return;
            }
            long now = _clock.GetTimestamp();
            foreach (WrapperAnnouncement announcement in message.Announcements)
            {
                WrapperMetrics.Record(WrapperEvent.AnnouncementReceived);
                ValueHash256 wrapperHash = announcement.Hash;
                peer.Known.Add(wrapperHash);
                if (!enabled || _store.Contains(wrapperHash)
                    || _tombstones.Contains(wrapperHash, now))
                {
                    if (_events.IsEnabled) RecordAnnouncement(peer, announcement, !enabled ? "ignored: EIP-8288 inactive"
                        : _store.Contains(wrapperHash) ? "already held" : "tombstoned: recently rejected or unresolved");
                    continue;
                }
                if (_fetches.TryGetValue(wrapperHash, out WrapperFetch? existing))
                {
                    existing.AddSource(peer, announcement.Length);
                    RecordAnnouncement(peer, announcement, "added as another source");
                    continue;
                }
                // The pending cache is bounded, and each peer holds at most a share of it, so one peer announcing wrappers it
                // never serves cannot crowd out the others. Announcements are not repeated, so excess ones are lost.
                if (_fetches.Count >= WrapperTransportLimits.MaxPendingAnnouncements
                    || peer.PendingFetches >= WrapperTransportLimits.MaxPendingAnnouncementsPerPeer)
                {
                    if (_events.IsEnabled) RecordAnnouncement(peer, announcement, _fetches.Count >= WrapperTransportLimits.MaxPendingAnnouncements
                        ? "dropped: pending announcement cache full" : "dropped: peer's share of pending announcements used");
                    continue;
                }
                RecordAnnouncement(peer, announcement, "queued");
                WrapperFetch fetch = new(announcement, now) { Announcer = peer };
                peer.PendingFetches++;
                fetch.AddSource(peer, announcement.Length);
                _fetches.Add(wrapperHash, fetch);
            }
            Schedule(outbox);
        }
        outbox.Flush();
    }

    private void RecordAnnouncement(WrapperPeer peer, WrapperAnnouncement announcement, string result) =>
        Record(ProofWrapperEventTypes.AnnouncementReceived, peer, announcement.Hash, "NewProofWrapperHashes", result: result, length: announcement.Length);

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
        foreach (WrapperFetch fetch in exhausted ?? []) Remove(fetch, ExhaustedReason);
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
            int length = fetch.LengthFrom(peer);
            if (batch.Count > 0 && bytes + length > Eth73Protocol.SoftResponseLimit) break;
            batch.Add(fetch);
            bytes += length;
        }
        // A response stops before the soft limit after its first wrapper, so the local response cap bounds the reservation.
        long reserved = Math.Min(bytes, Eth73Protocol.LocalMaxResponseBytes);
        if (batch.Count == 0 || peer.LastIssued == long.MaxValue || !TryCharge(peer, reserved)) return false;
        ValueHash256[] ids = new ValueHash256[batch.Count];
        WrappersRequest request = new(++peer.LastIssued, now, reserved, [.. batch]);
        for (int i = 0; i < ids.Length; i++)
        {
            WrapperFetch fetch = batch[i];
            fetch.Requested = true;
            fetch.Tried.Add(peer);
            fetch.Started ??= now;
            ids[i] = fetch.Announcement.Hash;
            if (_events.IsEnabled)
                Record(ProofWrapperEventTypes.WrapperRequested, peer, ids[i], "GetProofWrappers", request.Id, length: fetch.LengthFrom(peer),
                    count: fetch.Tried.Count);
        }
        peer.Requests.Add(request.Id, request);
        outbox.Send(peer, new GetProofWrappersMessage(request.Id, ids));
        WrapperMetrics.Record(WrapperEvent.Requested, ids.Length);
        return true;
    }

    /// <summary>Matches returned wrappers, in order, to distinct requested announcements, then queues them for validation.</summary>
    /// <remarks>Requested wrappers skipped before the last one returned were unavailable from this peer; those after it were cut
    /// by the soft limit and are requested again from the same peer.</remarks>
    internal void OnProofWrappers(WrapperPeer peer, ProofWrappersMessage message)
    {
        // Hash outside the lock: a response carries up to 2 MiB.
        (int Length, ValueHash256 Hash)[] identities = new (int, ValueHash256)[message.Wrappers.Length];
        for (int i = 0; i < identities.Length; i++) identities[i] = (message.Wrappers[i].Length, ValueKeccak.Compute(message.Wrappers[i]));
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            WrappersRequest? request = TakeRequest<WrappersRequest>(peer, message.RequestId, outbox);
            if (request is null)
            {
                outbox.Flush();
                return;
            }
            Release(peer, request.Reserved);
            WrapperFetch[] fetches = request.Fetches;
            int[] matched = new int[identities.Length];
            int cursor = 0;
            for (int i = 0; i < identities.Length; i++)
            {
                while (cursor < fetches.Length && (fetches[cursor].LengthFrom(peer) != identities[i].Length
                    || fetches[cursor].Announcement.Hash != identities[i].Hash)) cursor++;
                if (cursor == fetches.Length)
                {
                    foreach (WrapperFetch fetch in fetches) fetch.Requested = false;
                    Violation(peer, "a returned wrapper matches no requested announcement", outbox);
                    Schedule(outbox);
                    outbox.Flush();
                    return;
                }
                matched[i] = cursor++;
            }
            int last = identities.Length == 0 ? -1 : matched[^1];
            int next = 0;
            for (int position = 0; position < fetches.Length; position++)
            {
                WrapperFetch fetch = fetches[position];
                fetch.Requested = false;
                if (next < matched.Length && matched[next] == position)
                {
                    byte[] wrapper = message.Wrappers[next++];
                    peer.Known.Add(fetch.Announcement.Hash);
                    if (fetch.Removed || fetch.Wrapper is not null || !TryCharge(peer, wrapper.Length))
                    {
                        Record(ProofWrapperEventTypes.WrapperReceived, peer, fetch.Announcement.Hash, "ProofWrappers", message.RequestId,
                            fetch.Removed || fetch.Wrapper is not null ? "discarded: already received or abandoned" : "discarded: byte budget", length: wrapper.Length);
                        continue;
                    }
                    Record(ProofWrapperEventTypes.WrapperReceived, peer, fetch.Announcement.Hash, "ProofWrappers", message.RequestId,
                        "matched hash and announced length; queued for validation", length: wrapper.Length);
                    fetch.Wrapper = wrapper;
                    fetch.ChargedBytes = wrapper.Length;
                    fetch.Server = peer;
                    fetch.AddSource(peer, wrapper.Length);
                    Enqueue(fetch);
                }
                // A wrapper after the last returned one was cut by the soft limit: ask the same peer again.
                else if (position > last && identities.Length > 0)
                {
                    fetch.Tried.Remove(peer);
                    Record(ProofWrapperEventTypes.WrapperTruncated, peer, fetch.Announcement.Hash, "ProofWrappers", message.RequestId,
                        "after the last returned wrapper (soft response limit): re-request from the same peer");
                }
                else
                    Record(ProofWrapperEventTypes.WrapperUnavailable, peer, fetch.Announcement.Hash, "ProofWrappers", message.RequestId,
                        "omitted: unavailable from this peer, try another announcer");
            }
            WrapperMetrics.Record(WrapperEvent.Received, identities.Length);
            Schedule(outbox);
        }
        outbox.Flush();
    }

    /// <summary>Drops pending announcements after their lifetime and cancels fetches past the recovery deadline.</summary>
    private void ExpireFetches(long now, Outbox outbox)
    {
        List<WrapperFetch>? expired = null;
        foreach (WrapperFetch fetch in _fetches.Values)
        {
            bool lapsed = fetch.Started is { } started
                ? _clock.GetElapsedTime(started, now) >= WrapperTransportLimits.RecoveryDeadline
                : _clock.GetElapsedTime(fetch.Announced, now) >= WrapperTransportLimits.PendingLifetime;
            if (lapsed) (expired ??= []).Add(fetch);
        }
        foreach (WrapperFetch fetch in expired ?? [])
        {
            WrapperMetrics.Record(WrapperEvent.Expired);
            Record(ProofWrapperEventTypes.FetchAbandoned, wrapper: fetch.Announcement.Hash,
                result: fetch.Started is null ? "pending announcement lifetime passed" : "recovery deadline passed");
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
        if (reason == ExhaustedReason) Record(ProofWrapperEventTypes.FetchAbandoned, wrapper: fetch.Announcement.Hash, result: reason);
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
            if (_logger.IsDebug) _logger.Debug($"eth/73 recovery deadline passed for {fetch.Announcement}");
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

    /// <summary>Checks the canonical encoding, resolves every listed hash and fully validates the wrapper under EIP-8288.</summary>
    private async Task ValidateAsync(WrapperFetch fetch, CancellationToken cancellation)
    {
        byte[] wrapper = fetch.Wrapper!;
        ValueHash256[] hashes;
        try { hashes = WrapperBodies.ParseWrapper(wrapper); }
        catch (RlpException exception)
        {
            Reject(fetch, $"noncanonical wrapper: {exception.Message}");
            return;
        }
        Dictionary<ValueHash256, Transaction> recovered = [];
        List<int> missing = [];
        for (int i = 0; i < hashes.Length; i++)
        {
            if (_txPool.TryGetPendingTransaction(hashes[i], out _)) continue;
            byte[]? retained = null;
            lock (_gate) if (_store.TryGetEnvelope(hashes[i], out byte[] envelope)) retained = envelope;
            if (retained is not null && TryDecode(retained, hashes[i]) is { } transaction) recovered[hashes[i]] = transaction;
            else missing.Add(i);
        }
        if (_events.IsEnabled)
            Record(ProofWrapperEventTypes.ValidationStarted, fetch.Server, fetch.Announcement.Hash, count: missing.Count, details: _events.Describe(wrapper),
                detail: missing.Count == 0 ? "every listed envelope is pooled or cached"
                    : $"{missing.Count} of {hashes.Length} envelopes missing: recovering through GetPooledTransactions while checking the claimed proofs");
        if (missing.Count > 0)
        {
            // EIP-8437 lets the claimed-proof check run alongside envelope recovery. It does not validate the wrapper: its
            // transaction list is checked once every hash resolves. A failed check stops the recovery.
            using CancellationTokenSource recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Task<bool> recovering = RecoverEnvelopesAsync(fetch, hashes, missing, recovered, recovery.Token);
            (Verdict claims, string? claimsError) = await VerifyAsync(token => _wrappers.VerifyClaimedProofsOwnedAsync(wrapper, token), cancellation)
                .ConfigureAwait(false);
            if (claims != Verdict.Valid) await recovery.CancelAsync().ConfigureAwait(false);
            bool resolved;
            try { resolved = await recovering.ConfigureAwait(false); }
            catch (OperationCanceledException) when (recovery.IsCancellationRequested && !cancellation.IsCancellationRequested) { resolved = false; }
            if (claims != Verdict.Valid)
            {
                Conclude(fetch, claims, claimsError);
                return;
            }
            if (!resolved)
            {
                Conclude(fetch, Verdict.Unresolved, $"{missing.Count} envelopes unresolved");
                return;
            }
        }
        (Verdict verdict, string? error) = await VerifyAsync(token => _wrappers.AcceptOwnedDetailedAsync(wrapper, recovered, token), cancellation)
            .ConfigureAwait(false);
        Conclude(fetch, verdict, error);
    }

    private void Conclude(WrapperFetch fetch, Verdict verdict, string? error)
    {
        bool graceRefused = verdict == Verdict.Invalid && _wrappers.IsInVerifierTransition(_forkGraceBlocks);
        if (_events.IsEnabled)
            Record(ProofWrapperEventTypes.Verdict, fetch.Server, fetch.Announcement.Hash, result: graceRefused ? "GraceRefused" : verdict.ToString(),
                detail: error, durationMs: _clock.GetElapsedTime(fetch.Announced).TotalMilliseconds);
        switch (verdict)
        {
            case Verdict.Valid:
                WrapperMetrics.Record(WrapperEvent.Validated);
                if (_logger.IsDebug) _logger.Debug($"eth/73 validated {fetch.Announcement}");
                break;
            // Around a fork that changes the verifier, peers' target-fork estimates can differ by a slot: a proof failure is a
            // local refusal for the configured grace window (EIP-8437).
            case Verdict.Invalid when graceRefused:
                WrapperMetrics.Record(WrapperEvent.GraceRefused);
                lock (_gate) _tombstones.Add(fetch.Announcement.Hash, _clock.GetTimestamp()
                    + (long)(WrapperTransportLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
                if (_logger.IsDebug) _logger.Debug($"eth/73 refused {fetch.Announcement} within the fork grace window: {error}");
                break;
            case Verdict.Invalid:
                Reject(fetch, error ?? "invalid wrapper");
                break;
            case Verdict.Refused:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"eth/73 refused {fetch.Announcement}: {error}");
                break;
            default:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                lock (_gate) _tombstones.Add(fetch.Announcement.Hash, _clock.GetTimestamp()
                    + (long)(WrapperTransportLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
                if (_logger.IsDebug) _logger.Debug($"eth/73 could not complete {fetch.Announcement}: {error}");
                break;
        }
    }

    /// <summary>Tombstones an invalid wrapper and penalizes the peer that served it; the source should be deprioritized.</summary>
    private void Reject(WrapperFetch fetch, string reason)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            _tombstones.Add(fetch.Announcement.Hash);
            if (fetch.Server is { Closed: false } server) Violation(server, $"served invalid {fetch.Announcement}: {reason}", outbox);
        }
        outbox.Flush();
        WrapperMetrics.Record(WrapperEvent.Invalid);
        if (_logger.IsDebug) _logger.Debug($"eth/73 rejected {fetch.Announcement}: {reason}");
    }

    private async Task<(Verdict, string?)> VerifyAsync(Func<CancellationToken, Task<ProofWrapperAcceptance>> accept, CancellationToken cancellation)
    {
        // Recovery overlaps across bounded workers; proof verification and admission stay serialized.
        await _verification.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                ProofWrapperAcceptance result = await accept(cancellation).ConfigureAwait(false);
                switch (result.Status)
                {
                    // Pool policy is not a protocol matter: a valid proof stands even if its transactions were refused.
                    case ProofWrapperAcceptanceStatus.Accepted or ProofWrapperAcceptanceStatus.PoolRejected: return (Verdict.Valid, null);
                    case ProofWrapperAcceptanceStatus.Invalid: return (Verdict.Invalid, result.Result.Error);
                    case ProofWrapperAcceptanceStatus.Refused: return (Verdict.Refused, result.Result.Error);
                    case ProofWrapperAcceptanceStatus.Busy when attempt < MaxBusyRetries:
                        await Task.Delay(WrapperTransportLimits.BusyRetry, cancellation).ConfigureAwait(false);
                        continue;
                    default: return (Verdict.LocalFailure, result.Result.Error);
                }
            }
        }
        finally { _verification.Release(); }
    }

    // ---- Envelope recovery --------------------------------------------------------------------------------------------

    /// <summary>Recovers missing envelopes through GetPooledTransactions from the peer that served the wrapper, then other
    /// announcers, within the source budget and the recovery deadline.</summary>
    /// <remarks>As for any PooledTransactions response, requested envelopes skipped before the last one returned are
    /// unavailable from that source and go to the next one, while those after it were cut by the response limit and are
    /// requested from the same source again.</remarks>
    /// <returns>Whether every missing envelope was recovered.</returns>
    private async Task<bool> RecoverEnvelopesAsync(WrapperFetch fetch, ValueHash256[] hashes, List<int> missing,
        Dictionary<ValueHash256, Transaction> recovered, CancellationToken cancellation)
    {
        List<WrapperPeer> sources;
        lock (_gate)
        {
            sources = fetch.Server is { } server ? [server] : [];
            foreach (WrapperPeer source in fetch.Sources)
                if (!sources.Contains(source)) sources.Add(source);
            if (sources.Count > WrapperTransportLimits.MaxSourcesPerWrapper) sources.RemoveRange(WrapperTransportLimits.MaxSourcesPerWrapper, sources.Count - WrapperTransportLimits.MaxSourcesPerWrapper);
        }
        // Wrapper hashes are distinct, so the outstanding set is too.
        List<ValueHash256> outstanding = new(missing.Count);
        foreach (int index in missing) outstanding.Add(hashes[index]);
        long started = _clock.GetTimestamp();
        int requests = 0;
        foreach (WrapperPeer source in sources)
        {
            List<ValueHash256> toAsk = [.. outstanding];
            while (toAsk.Count > 0)
            {
                ValueHash256[] requested = [.. toAsk.GetRange(0, Math.Min(Eth73Protocol.MaxEnvelopesPerRequest, toAsk.Count))];
                EnvelopesRequest? request = await SendEnvelopeRequestAsync(source, requested, cancellation).ConfigureAwait(false);
                if (request is null) break;
                requests++;
                if (_events.IsEnabled)
                    Record(ProofWrapperEventTypes.EnvelopeRequested, source, fetch.Announcement.Hash, "GetPooledTransactions", request.Id,
                        count: requests, transactions: ToHashes(requested),
                        detail: source == fetch.Server ? "asking the peer that served the wrapper" : "asking another announcer");
                long requestStarted = _clock.GetTimestamp();
                Transaction[]? envelopes;
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(WrapperTransportLimits.RequestTimeout);
                    try { envelopes = await request.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { envelopes = null; }
                }
                // No response, or an empty one, means unavailable from this source: no penalty, try another.
                bool accepted = envelopes is { Length: > 0 } && AcceptEnvelopes(fetch, envelopes, recovered);
                int last = accepted ? Array.FindLastIndex(requested, recovered.ContainsKey) : -1;
                if (_events.IsEnabled)
                {
                    int omitted = 0;
                    for (int i = 0; i < last; i++) if (!recovered.ContainsKey(requested[i])) omitted++;
                    Record(ProofWrapperEventTypes.EnvelopeResponse, source, fetch.Announcement.Hash, "PooledTransactions", request.Id,
                        envelopes is null ? "no usable response" : envelopes.Length == 0 ? "empty: unavailable from this peer"
                            : !accepted ? "discarded: byte budget" : $"returned {envelopes.Length} of {requested.Length}",
                        omitted > 0 ? $"{omitted} omitted before the last returned: unavailable here, try another announcer"
                            : last >= 0 && last < requested.Length - 1 ? $"{requested.Length - 1 - last} after the last returned: truncated, ask again" : null,
                        count: envelopes?.Length ?? 0, durationMs: _clock.GetElapsedTime(requestStarted).TotalMilliseconds,
                        transactions: envelopes is { Length: > 0 } ? Array.ConvertAll(envelopes, static t => t.Hash!) : null);
                }
                if (!accepted) break;
                toAsk.RemoveRange(0, last + 1);
            }
            outstanding.RemoveAll(recovered.ContainsKey);
            if (outstanding.Count == 0) break;
        }
        if (_events.IsEnabled)
            Record(ProofWrapperEventTypes.EnvelopeRecovery, fetch.Server, fetch.Announcement.Hash,
                result: outstanding.Count == 0 ? "complete" : "incomplete: wrapper stays unresolved, not invalid",
                detail: $"recovered {missing.Count - outstanding.Count} of {missing.Count} envelopes from {sources.Count} sources",
                count: requests, durationMs: _clock.GetElapsedTime(started).TotalMilliseconds);
        if (_logger.IsDebug)
            _logger.Debug($"eth/73 recovered {missing.Count - outstanding.Count} of {missing.Count} envelopes for {fetch.Announcement} with " +
                $"{requests} GetPooledTransactions in {_clock.GetElapsedTime(started).TotalMilliseconds:F0} ms");
        return outstanding.Count == 0;
    }

    private static Hash256[] ToHashes(ValueHash256[] hashes) => Array.ConvertAll(hashes, static hash => new Hash256(hash));

    /// <summary>Sends one GetPooledTransactions once the source has a free request slot and the byte budgets allow it.</summary>
    private async Task<EnvelopesRequest?> SendEnvelopeRequestAsync(WrapperPeer source, ValueHash256[] hashes, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Outbox outbox = new();
            EnvelopesRequest? request = null;
            lock (_gate)
            {
                if (source.Closed || _disposed) return null;
                if (source.CanRequest && source.LastIssued != long.MaxValue && TryCharge(source, Eth73Protocol.LocalMaxResponseBytes))
                {
                    request = new(++source.LastIssued, _clock.GetTimestamp(), Eth73Protocol.LocalMaxResponseBytes, hashes);
                    source.Requests.Add(request.Id, request);
                    ArrayPoolList<ValueHash256> wire = new(hashes.Length);
                    wire.AddRange(hashes);
                    outbox.Send(source, new GetPooledTransactionsMessage66(wire) { RequestId = request.Id });
                    WrapperMetrics.Record(WrapperEvent.EnvelopeRequested, hashes.Length);
                }
            }
            outbox.Flush();
            if (request is not null) return request;
            await Task.Delay(WrapperTransportLimits.BusyRetry, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Charges the retained bytes of recovered transactions, already matched to their request.</summary>
    /// <returns>False when the fetch was dropped or the byte budget is spent.</returns>
    private bool AcceptEnvelopes(WrapperFetch fetch, Transaction[] transactions, Dictionary<ValueHash256, Transaction> recovered)
    {
        long bytes = 0;
        foreach (Transaction transaction in transactions) bytes += transaction.GetLength();
        bool accepted;
        lock (_gate)
        {
            accepted = !fetch.Removed && fetch.Server is { } server && TryCharge(server, bytes);
            if (accepted) fetch.ChargedBytes += bytes;
        }
        if (!accepted) return false;
        foreach (Transaction transaction in transactions) recovered[transaction.Hash!.ValueHash256] = transaction;
        WrapperMetrics.Record(WrapperEvent.EnvelopeRecovered, transactions.Length);
        return true;
    }

    /// <summary>Whether <paramref name="requestId"/> is an outstanding envelope-recovery request to <paramref name="peer"/>,
    /// whose PooledTransactions response the wrapper transport handles.</summary>
    internal bool IsEnvelopeRequest(WrapperPeer peer, long requestId)
    {
        lock (_gate) return !peer.Closed && peer.Requests.TryGetValue(requestId, out WrapperRequest? request) && request is EnvelopesRequest;
    }

    /// <summary>Handles a PooledTransactions response to an envelope-recovery request.</summary>
    /// <remarks>
    /// Each returned transaction is matched by its transaction hash, Keccak-256 of its EIP-2718 envelope without any network
    /// wrapper, to distinct requested hashes in request order; omissions and truncation are allowed. Malformed, repeated,
    /// reordered or unrequested entries are discarded as unmatched data, and this client disconnects the peer for them.
    /// </remarks>
    internal void OnPooledEnvelopes(WrapperPeer peer, long requestId, byte[][] items)
    {
        // Decode and hash outside the lock: a response carries up to 2 MiB.
        Transaction[]? transactions = new Transaction[items.Length];
        ValueHash256[] hashes = new ValueHash256[items.Length];
        for (int i = 0; i < items.Length && transactions is not null; i++)
        {
            if (DecodePooled(items[i]) is { } transaction)
            {
                transactions[i] = transaction;
                hashes[i] = transaction.Hash!.ValueHash256;
            }
            else transactions = null;
        }
        Outbox outbox = new();
        EnvelopesRequest? request;
        bool matches = false;
        lock (_gate)
        {
            if (peer.Closed) return;
            // Only outstanding envelope requests are routed here; one that expired meanwhile is discarded.
            request = peer.Requests.Remove(requestId, out WrapperRequest? taken) ? taken as EnvelopesRequest : null;
            if (request is not null)
            {
                Release(peer, request.Reserved);
                matches = transactions is not null && MatchesInOrder(hashes, request.Hashes);
                if (!matches)
                    Violation(peer, transactions is null ? "PooledTransactions carries a malformed transaction"
                        : "PooledTransactions repeats, reorders or adds envelopes", outbox);
            }
            Schedule(outbox);
        }
        outbox.Flush();
        request?.Completion.TrySetResult(matches ? transactions : null);
    }

    /// <summary>Decodes one PooledTransactions item, network wrapper included, with its transaction hash.</summary>
    internal static Transaction? DecodePooled(byte[] item)
    {
        try
        {
            Transaction transaction = TxDecoder.Instance.DecodeCompleteNotNull(item, RlpBehaviors.InMempoolForm);
            transaction.Hash ??= transaction.CalculateHash();
            return transaction;
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether each of <paramref name="returned"/> matches a distinct entry of <paramref name="requested"/>, in order.</summary>
    internal static bool MatchesInOrder(ReadOnlySpan<ValueHash256> returned, ReadOnlySpan<ValueHash256> requested)
    {
        int cursor = 0;
        foreach (ValueHash256 hash in returned)
        {
            while (cursor < requested.Length && requested[cursor] != hash) cursor++;
            if (cursor++ == requested.Length) return false;
        }
        return true;
    }

    private static Transaction? TryDecode(byte[] envelope, in ValueHash256 hash)
    {
        try
        {
            Transaction transaction = TxDecoder.Instance.DecodeCompleteNotNull(envelope, RlpBehaviors.SkipTypedWrapping);
            transaction.Hash = new Hash256(hash);
            return transaction;
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException)
        {
            return null;
        }
    }
}
