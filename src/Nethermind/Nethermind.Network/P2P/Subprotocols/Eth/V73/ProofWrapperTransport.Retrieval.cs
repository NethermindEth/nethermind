// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

public sealed partial class ProofWrapperTransport
{
    private const int MaxBusyRetries = 50;

    /// <summary>The outcome of validating a received wrapper.</summary>
    /// <remarks><see cref="Refused"/> and <see cref="LocalFailure"/> are local inabilities to finish validation and
    /// <see cref="Unresolved"/> a failed envelope recovery; none establishes invalidity or penalizes a peer.</remarks>
    private enum Verdict { Valid, Invalid, LocalFailure, Refused, Unresolved }

    // ---- Announcements and wrapper requests ---------------------------------------------------------------------------

    /// <summary>Records announced wrappers this node wants; announcements establish no validity and require no action.</summary>
    internal void OnAnnounce(WrapperPeer peer, AnnounceProofWrappersMessage message)
    {
        bool enabled = IsEnabled;
        Outbox outbox = new();
        lock (_gate)
        {
            // Announcements beyond the rate limit are ignored, not penalized.
            if (peer.Closed || !peer.AnnouncementsIn.TryTake(1)) return;
            long now = _clock.GetTimestamp();
            foreach (WrapperDescriptor descriptor in message.Descriptors)
            {
                WrapperMetrics.Record(WrapperEvent.AnnouncementReceived);
                ValueHash256 objectId = descriptor.ObjectId;
                peer.Known.Add(objectId);
                if (!enabled || _store.Contains(objectId)
                    || _tombstones.Contains(objectId, now)) continue;
                if (_fetches.TryGetValue(objectId, out WrapperFetch? existing))
                {
                    existing.AddSource(peer);
                    continue;
                }
                // The pending cache is bounded; announcements are not repeated, so excess ones are lost.
                if (_fetches.Count >= WrapperTransportLimits.MaxPendingDescriptors) continue;
                WrapperFetch fetch = new(descriptor, now);
                fetch.AddSource(peer);
                _fetches.Add(objectId, fetch);
            }
            Schedule(outbox);
        }
        outbox.Flush();
    }

    /// <summary>Requests queued wrappers from their next untried source, up to four outstanding requests per peer.</summary>
    private void Schedule(Outbox outbox)
    {
        if (_disposed || _peers.Count == 0) return;
        long now = _clock.GetTimestamp();
        List<WrapperFetch>? exhausted = null;
        foreach (WrapperFetch fetch in _fetches.Values)
            if (!fetch.Requested && fetch.Wrapper is null && fetch.NextSource() is null) (exhausted ??= []).Add(fetch);
        foreach (WrapperFetch fetch in exhausted ?? []) Remove(fetch, "every source tried");
        int count = _peers.Count;
        int first = _nextPeer % count;
        for (int p = 0; p < count; p++)
        {
            WrapperPeer peer = _peers[(first + p) % count];
            while (peer.CanRequest && RequestFrom(peer, now, outbox)) _nextPeer = (first + p + 1) % count;
        }
    }

    /// <summary>Sends one GetProofWrappers for wrappers whose next source is <paramref name="peer"/>, within the soft limit.</summary>
    private bool RequestFrom(WrapperPeer peer, long now, Outbox outbox)
    {
        List<WrapperFetch> batch = [];
        long bytes = 0;
        foreach (WrapperFetch fetch in _fetches.Values)
        {
            if (fetch.Requested || fetch.Wrapper is not null || fetch.NextSource() != peer) continue;
            if (batch.Count > 0 && bytes + fetch.Descriptor.ByteLength > Eth73Protocol.SoftResponseLimit) continue;
            batch.Add(fetch);
            bytes += fetch.Descriptor.ByteLength;
            if (batch.Count == Eth73Protocol.MaxWrappersPerRequest) break;
        }
        // A response stops before the soft limit after its first wrapper, so the local response cap bounds the reservation.
        long reserved = Math.Min(bytes, Eth73Protocol.LocalMaxResponseBytes);
        if (batch.Count == 0 || peer.LastIssued == long.MaxValue || !TryCharge(peer, reserved)) return false;
        ValueHash256[] ids = new ValueHash256[batch.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            WrapperFetch fetch = batch[i];
            fetch.Requested = true;
            fetch.Tried.Add(peer);
            fetch.Started ??= now;
            ids[i] = fetch.Descriptor.ObjectId;
        }
        WrappersRequest request = new(++peer.LastIssued, now, reserved, [.. batch]);
        peer.Requests.Add(request.Id, request);
        outbox.Send(peer, new GetProofWrappersMessage(request.Id, ids));
        WrapperMetrics.Record(WrapperEvent.Requested, ids.Length);
        return true;
    }

    /// <summary>Matches returned wrappers, in order, to distinct requested descriptors, then queues them for validation.</summary>
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
                while (cursor < fetches.Length && (fetches[cursor].Descriptor.ByteLength != identities[i].Length
                    || fetches[cursor].Descriptor.ContentHash != identities[i].Hash)) cursor++;
                if (cursor == fetches.Length)
                {
                    foreach (WrapperFetch fetch in fetches) fetch.Requested = false;
                    Violation(peer, "a returned wrapper matches no requested descriptor", outbox);
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
                    peer.Known.Add(fetch.Descriptor.ObjectId);
                    if (fetch.Removed || fetch.Wrapper is not null || !TryCharge(peer, wrapper.Length)) continue;
                    fetch.Wrapper = wrapper;
                    fetch.ChargedBytes = wrapper.Length;
                    fetch.Server = peer;
                    fetch.AddSource(peer);
                    Enqueue(fetch);
                }
                // A wrapper after the last returned one was cut by the soft limit: ask the same peer again.
                else if (position > last && identities.Length > 0) fetch.Tried.Remove(peer);
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
            // Validation releases its own fetch once its cancelled work unwinds.
            if (fetch.Processing is { } processing) outbox.Cancel(processing);
            else Remove(fetch, "deadline");
        }
    }

    private void Remove(WrapperFetch fetch, string reason)
    {
        if (_fetches.TryGetValue(fetch.Descriptor.ObjectId, out WrapperFetch? current) && current == fetch)
            _fetches.Remove(fetch.Descriptor.ObjectId);
        Drop(fetch);
        if (_logger.IsTrace) _logger.Trace($"eth/73 stopped fetching {fetch.Descriptor}: {reason}");
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
            if (_logger.IsDebug) _logger.Debug($"eth/73 recovery deadline passed for {fetch.Descriptor}");
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"eth/73 validation of {fetch.Descriptor} failed: {exception.Message}");
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
        if (missing.Count > 0)
        {
            // Verify the claimed proofs before fetching anything, so an invalid proof costs no recovery. This does not validate
            // the wrapper: its transaction list is checked once every hash resolves.
            (Verdict claims, string? claimsError) = await VerifyAsync(token => _wrappers.VerifyClaimedProofsOwnedAsync(wrapper, token), cancellation)
                .ConfigureAwait(false);
            if (claims != Verdict.Valid)
            {
                Conclude(fetch, claims, claimsError);
                return;
            }
            if (!await RecoverEnvelopesAsync(fetch, hashes, missing, recovered, cancellation).ConfigureAwait(false))
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
        switch (verdict)
        {
            case Verdict.Valid:
                WrapperMetrics.Record(WrapperEvent.Validated);
                if (_logger.IsDebug) _logger.Debug($"eth/73 validated {fetch.Descriptor}");
                break;
            // Around a fork that changes the verifier, peers can disagree about the fork of the next block: a proof failure is a
            // local refusal for the configured grace window (EIP-8437).
            case Verdict.Invalid when _wrappers.IsInVerifierTransition(_forkGraceBlocks):
                WrapperMetrics.Record(WrapperEvent.GraceRefused);
                lock (_gate) _tombstones.Add(fetch.Descriptor.ObjectId, _clock.GetTimestamp()
                    + (long)(WrapperTransportLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
                if (_logger.IsDebug) _logger.Debug($"eth/73 refused {fetch.Descriptor} within the fork grace window: {error}");
                break;
            case Verdict.Invalid:
                Reject(fetch, error ?? "invalid wrapper");
                break;
            case Verdict.Refused:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                if (_logger.IsDebug) _logger.Debug($"eth/73 refused {fetch.Descriptor}: {error}");
                break;
            default:
                WrapperMetrics.Record(WrapperEvent.LocalFailure);
                lock (_gate) _tombstones.Add(fetch.Descriptor.ObjectId, _clock.GetTimestamp()
                    + (long)(WrapperTransportLimits.SoftTombstone.TotalSeconds * _clock.TimestampFrequency));
                if (_logger.IsDebug) _logger.Debug($"eth/73 could not complete {fetch.Descriptor}: {error}");
                break;
        }
    }

    /// <summary>Tombstones an invalid wrapper and penalizes the peer that served it; the source should be deprioritized.</summary>
    private void Reject(WrapperFetch fetch, string reason)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            _tombstones.Add(fetch.Descriptor.ObjectId);
            if (fetch.Server is { Closed: false } server) Violation(server, $"served invalid {fetch.Descriptor}: {reason}", outbox);
        }
        outbox.Flush();
        WrapperMetrics.Record(WrapperEvent.Invalid);
        if (_logger.IsDebug) _logger.Debug($"eth/73 rejected {fetch.Descriptor}: {reason}");
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

    /// <summary>Recovers missing envelopes from the peer that served the wrapper, then other announcers, within the source
    /// budget and the recovery deadline.</summary>
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
        int resolved = 0;
        foreach (WrapperPeer source in sources)
        {
            while (resolved < missing.Count)
            {
                int[] indices = [.. missing.GetRange(resolved, Math.Min(Eth73Protocol.MaxEnvelopesPerRequest, missing.Count - resolved))];
                EnvelopesRequest? request = await SendEnvelopeRequestAsync(fetch, source, indices, cancellation).ConfigureAwait(false);
                if (request is null) break;
                WrapperEnvelopesMessage? response;
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(WrapperTransportLimits.RequestTimeout);
                    try { response = await request.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { response = null; }
                }
                // No response, or an empty one, means unavailable from this source: no penalty, try another.
                if (response is not { Envelopes.Length: > 0 }) break;
                if (!AcceptEnvelopes(fetch, source, hashes, indices, response, recovered)) break;
                resolved += response.Envelopes.Length;
            }
            if (resolved == missing.Count) return true;
        }
        return false;
    }

    /// <summary>Sends one GetWrapperEnvelopes once the source has a free request slot and the byte budgets allow it.</summary>
    private async Task<EnvelopesRequest?> SendEnvelopeRequestAsync(WrapperFetch fetch, WrapperPeer source, int[] indices, CancellationToken cancellation)
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
                    request = new(++source.LastIssued, _clock.GetTimestamp(), Eth73Protocol.LocalMaxResponseBytes);
                    source.Requests.Add(request.Id, request);
                    outbox.Send(source, new GetWrapperEnvelopesMessage(request.Id, fetch.Descriptor.ObjectId, indices));
                }
            }
            outbox.Flush();
            if (request is not null) return request;
            await Task.Delay(WrapperTransportLimits.BusyRetry, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Checks a WrapperEnvelopes prefix against the wrapper's listed hashes and charges the retained bytes.</summary>
    /// <returns>False when the response was invalid peer data, which penalizes its source.</returns>
    private bool AcceptEnvelopes(WrapperFetch fetch, WrapperPeer source, ValueHash256[] hashes, int[] indices, WrapperEnvelopesMessage response,
        Dictionary<ValueHash256, Transaction> recovered)
    {
        string? error = response.Envelopes.Length > indices.Length ? "more envelopes than requested" : null;
        List<Transaction> transactions = new(response.Envelopes.Length);
        long bytes = 0;
        for (int k = 0; error is null && k < response.Envelopes.Length; k++)
        {
            byte[] envelope = response.Envelopes[k];
            ValueHash256 hash = hashes[indices[k]];
            if (ValueKeccak.Compute(envelope) != hash) error = $"envelope {indices[k]} does not hash to the listed transaction";
            else if (TryDecode(envelope, hash) is { } transaction)
            {
                transactions.Add(transaction);
                bytes += envelope.Length;
            }
            else error = $"envelope {indices[k]} is not a canonical transaction";
        }
        Outbox outbox = new();
        bool accepted;
        lock (_gate)
        {
            accepted = error is null && !fetch.Removed && fetch.Server is { } server && TryCharge(server, bytes);
            if (accepted) fetch.ChargedBytes += bytes;
            if (error is not null && !source.Closed) Violation(source, error, outbox);
        }
        outbox.Flush();
        if (!accepted) return false;
        foreach (Transaction transaction in transactions) recovered[transaction.Hash!.ValueHash256] = transaction;
        return true;
    }

    internal void OnWrapperEnvelopes(WrapperPeer peer, WrapperEnvelopesMessage message)
    {
        Outbox outbox = new();
        EnvelopesRequest? request;
        lock (_gate)
        {
            if (peer.Closed) return;
            request = TakeRequest<EnvelopesRequest>(peer, message.RequestId, outbox);
            if (request is not null) Release(peer, request.Reserved);
            Schedule(outbox);
        }
        outbox.Flush();
        request?.Completion.TrySetResult(message);
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
