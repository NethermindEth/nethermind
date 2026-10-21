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
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// EIP-8437 wrapper propagation shared by every <c>eth/73</c> connection: announcements, whole-wrapper retrieval, envelope
/// recovery through GetPooledTransactions, validation hand-off, and serving of validated wrappers and of the envelopes they list.
/// </summary>
/// <remarks>
/// All peer, request and fetch state is guarded by one lock; messages and penalties are sent after it is released. No
/// wrapper is announced, served, admitted or reaggregated before it is fully validated under EIP-8288, and none is announced
/// or served once the target fork's verifier differs from the one it was validated under: on a verifier change every
/// mode-1 wrapper is dropped and a mode-0 wrapper is kept only if it re-validates.
/// </remarks>
public sealed partial class ProofWrapperTransport : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly ProofWrapperService _wrappers;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<WrapperPeer> _peers = [];
    private readonly WrapperStore _store = new();
    private readonly Dictionary<ValueHash256, WrapperFetch> _fetches = [];
    private readonly WrapperBoundedSet _tombstones = new(WrapperTransportLimits.MaxTombstones);
    private readonly RetainedEnvelopes _retained = new();
    private readonly Queue<WrapperFetch> _validation = [];
    private readonly SemaphoreSlim _verification = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer? _timer;
    private long _chargedBytes;
    private int _validationWorkers;
    private int _nextPeer;
    private bool _disposed;
    private readonly long _forkGraceBlocks;
    private Eip8288Verifier? _verifier;

    public ProofWrapperTransport(ITxPool txPool, ProofWrapperService wrappers, ILogManager logManager, TimeProvider? timeProvider = null,
        INetworkConfig? networkConfig = null)
    {
        _txPool = txPool;
        _wrappers = wrappers;
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logManager.GetClassLogger<ProofWrapperTransport>();
        _forkGraceBlocks = networkConfig?.ProofWrapperForkGraceBlocks ?? 64;
        _verifier = _wrappers.RefreshVerifier();
        _wrappers.WrapperValidated += OnWrapperValidated;
        _wrappers.VerifierChanged += OnVerifierChanged;
        // A chain without a scheduled EIP-8288 fork runs no wrapper timer.
        _timer = _wrappers.IsScheduled
            ? _clock.CreateTimer(static state => ((ProofWrapperTransport)state!).Tick(), this, WrapperTransportLimits.Tick, WrapperTransportLimits.Tick)
            : null;
    }

    /// <summary>Whether the head activates EIP-8288; the transport is inert otherwise.</summary>
    public bool IsEnabled => _wrappers.IsEnabled;

    internal int PeerCount { get { lock (_gate) return _peers.Count; } }
    internal int FetchCount { get { lock (_gate) return _fetches.Count; } }
    internal int StoredObjects { get { lock (_gate) return _store.Count; } }
    internal long ChargedBytes { get { lock (_gate) return _chargedBytes; } }
    internal bool IsStored(in ValueHash256 wrapperHash) { lock (_gate) return _store.Contains(wrapperHash); }
    internal bool IsOffered(in ValueHash256 wrapperHash) { lock (_gate) return TryGetOffered(wrapperHash, out _); }

    /// <summary>A held wrapper validated under the current target fork's verifier, which alone may be announced or served.</summary>
    private bool TryGetOffered(in ValueHash256 wrapperHash, out WrapperStore.Entry entry)
        => _store.TryGet(wrapperHash, out entry) && entry.Verifier == _verifier;
    internal bool IsTombstoned(in ValueHash256 wrapperHash) { lock (_gate) return _tombstones.Contains(wrapperHash, _clock.GetTimestamp()); }

    /// <summary>Registers an <c>eth/73</c> peer once its Status is accepted.</summary>
    /// <remarks>Peers negotiate no verifier: each validates under its own target fork. Null while EIP-8288 is inactive, which
    /// keeps the wrapper messages off the connection.</remarks>
    internal WrapperPeer? Accept(IWrapperLink link)
    {
        if (!IsEnabled) return null;
        Outbox outbox = new();
        WrapperPeer peer;
        lock (_gate)
        {
            if (_disposed) return null;
            peer = new(link, _clock);
            _peers.Add(peer);
            // Peers announce the wrappers they offer on connection, most recent first, within the announcement rate limit.
            foreach (WrapperStore.Entry entry in _store.Recent())
                if (entry.Verifier == _verifier) peer.ToAnnounce.Add(entry.Announcement);
            FlushAnnouncements(peer, outbox);
        }
        outbox.Flush();
        return peer;
    }

    internal void Remove(WrapperPeer peer)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            RemoveCore(peer, outbox);
            Schedule(outbox);
        }
        outbox.Flush();
    }

    private void RemoveCore(WrapperPeer peer, Outbox outbox)
    {
        if (peer.Closed) return;
        peer.Closed = true;
        _peers.Remove(peer);
        foreach (WrapperRequest request in peer.Requests.Values) Abandon(peer, request, outbox);
        peer.Requests.Clear();
        peer.ToAnnounce.Clear();
        _retained.RemoveFrom(peer);
        foreach (WrapperFetch fetch in _fetches.Values) fetch.Sources.Remove(peer);
    }

    /// <summary>Releases an outstanding request's reservation; its wanted wrappers go back to the queue.</summary>
    private void Abandon(WrapperPeer peer, WrapperRequest request, Outbox outbox)
    {
        Release(peer, request.Reserved);
        switch (request)
        {
            case WrappersRequest wrappers:
                foreach (WrapperFetch fetch in wrappers.Fetches) fetch.Requested = false;
                break;
            case EnvelopesRequest envelopes:
                outbox.Complete(envelopes.Completion);
                break;
        }
    }

    private bool TryCharge(WrapperPeer peer, long bytes)
    {
        if (bytes < 0 || bytes > WrapperTransportLimits.MaxGlobalBytes - _chargedBytes
            || bytes > WrapperTransportLimits.MaxPeerBytes - peer.ChargedBytes) return false;
        _chargedBytes += bytes;
        peer.ChargedBytes += bytes;
        return true;
    }

    private void Release(WrapperPeer peer, long bytes)
    {
        _chargedBytes -= bytes;
        peer.ChargedBytes -= bytes;
    }

    /// <summary>Invalid peer data or a protocol violation: the contribution is discarded and the peer disconnected.</summary>
    private void Violation(WrapperPeer peer, string reason, Outbox outbox)
    {
        WrapperMetrics.Record(WrapperEvent.Violation);
        if (_logger.IsDebug) _logger.Debug($"eth/73 wrapper violation by {peer}: {reason}");
        RemoveCore(peer, outbox);
        outbox.Penalize(peer, $"eth/73: {reason}");
    }

    /// <summary>Resolves a response's request; null when it is discarded or the peer was penalized.</summary>
    private T? TakeRequest<T>(WrapperPeer peer, long requestId, Outbox outbox) where T : WrapperRequest
    {
        if (peer.Requests.Remove(requestId, out WrapperRequest? request))
        {
            if (request is T matching) return matching;
            Violation(peer, $"response type does not match request {requestId}", outbox);
            return null;
        }
        // An issued request that expired is discarded; one never issued is a violation.
        if (requestId <= 0 || requestId > peer.LastIssued) Violation(peer, $"response to unissued request {requestId}", outbox);
        return null;
    }

    private void Tick()
    {
        // Applies the EIP-8288 upgrade rule, through VerifierChanged, once the head reaches a fork with another verifier.
        // Idle while EIP-8288 is inactive: no peer registers then.
        if (!IsEnabled && PeerCount == 0) return;
        Eip8288Verifier? refreshed;
        try { refreshed = _wrappers.RefreshVerifier(); }
        catch (Exception exception)
        {
            // An exception escaping a timer callback ends the process; the upgrade rule is retried on the next tick.
            if (_logger.IsError) _logger.Error("eth/73 could not apply the EIP-8288 upgrade rule", exception);
            refreshed = _wrappers.CurrentVerifier;
        }
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed) return;
            // Read again under the lock: a concurrent refresh may have applied a newer verifier, and OnVerifierChanged must not
            // be overwritten with the older value this thread read.
            _verifier = refreshed is null ? null : _wrappers.CurrentVerifier;
            long now = _clock.GetTimestamp();
            foreach (WrapperPeer peer in _peers.ToArray())
            {
                List<WrapperRequest>? expired = null;
                foreach (WrapperRequest request in peer.Requests.Values)
                    if (_clock.GetElapsedTime(request.Created, now) >= WrapperTransportLimits.RequestTimeout) (expired ??= []).Add(request);
                foreach (WrapperRequest request in expired ?? [])
                {
                    peer.Requests.Remove(request.Id);
                    WrapperMetrics.Record(WrapperEvent.RequestExpired);
                    Abandon(peer, request, outbox);
                }
                FlushAnnouncements(peer, outbox);
            }
            ExpireFetches(now, outbox);
            ExpireRetained(now, outbox);
            Schedule(outbox);
            WrapperMetrics.Gauges(_peers.Count, _store.Count, _fetches.Count, _chargedBytes);
        }
        outbox.Flush();
    }

    public void Dispose()
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (WrapperPeer peer in _peers.ToArray()) RemoveCore(peer, outbox);
            foreach (WrapperFetch fetch in _fetches.Values) if (fetch.Processing is null) Drop(fetch);
            if (_validationWorkers == 0) _verification.Dispose();
        }
        outbox.Flush();
        _wrappers.WrapperValidated -= OnWrapperValidated;
        _wrappers.VerifierChanged -= OnVerifierChanged;
        _timer?.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Messages, completions and penalties collected under the lock and acted on after it is released.</summary>
    private sealed class Outbox
    {
        private List<WrapperPeer>? _recipients;
        private List<(WrapperPeer Peer, string Reason)>? _penalties;
        private List<TaskCompletionSource<Transaction[]?>>? _completions;
        private List<CancellationTokenSource>? _cancellations;
        private List<(WrapperPeer Peer, string Reason)>? _scores;

        public void Send(WrapperPeer peer, P2PMessage message)
        {
            peer.Outgoing.Enqueue(message);
            if (!(_recipients ??= []).Contains(peer)) _recipients.Add(peer);
        }

        public void Penalize(WrapperPeer peer, string reason) => (_penalties ??= []).Add((peer, reason));

        public void LowerScore(WrapperPeer peer, string reason) => (_scores ??= []).Add((peer, reason));

        /// <summary>Ends an envelope request without a response, after the lock is released.</summary>
        public void Complete(TaskCompletionSource<Transaction[]?> completion) => (_completions ??= []).Add(completion);

        /// <summary>Cancels work after the lock is released, since cancellation callbacks may resume awaiting code inline.</summary>
        public void Cancel(CancellationTokenSource source) => (_cancellations ??= []).Add(source);

        public void Flush()
        {
            foreach (TaskCompletionSource<Transaction[]?> completion in _completions ?? []) completion.TrySetResult(null);
            foreach (CancellationTokenSource source in _cancellations ?? [])
            {
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            foreach (WrapperPeer peer in _recipients ?? []) Drain(peer);
            foreach ((WrapperPeer peer, string reason) in _scores ?? []) peer.Link.LowerScore(reason);
            foreach ((WrapperPeer peer, string reason) in _penalties ?? []) peer.Link.Penalize(reason);
        }

        /// <summary>Sends the peer's queued messages unless another thread is already sending them, which then sends these too.</summary>
        private static void Drain(WrapperPeer peer)
        {
            while (!peer.Outgoing.IsEmpty && Interlocked.CompareExchange(ref peer.Sending, 1, 0) == 0)
            {
                try
                {
                    while (peer.Outgoing.TryDequeue(out P2PMessage? message))
                    {
                        if (!peer.Closed) peer.Link.Send(message);
                        else message.Dispose();
                    }
                }
                finally { Volatile.Write(ref peer.Sending, 0); }
            }
        }
    }

    // ---- Publication -------------------------------------------------------------------------------------------------

    private void OnWrapperValidated(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction> resolved, Eip8288Verifier verifier) =>
        _ = Task.Run(() =>
        {
            try { Publish(wrapper, verifier, resolved); }
            catch (Exception exception) when (exception is ArgumentException or RlpException or InvalidOperationException)
            {
                if (_logger.IsDebug) _logger.Debug($"eth/73 could not offer a wrapper: {exception.Message}");
            }
        });

    /// <summary>Stores and announces a locally aggregated wrapper, produced under the current target fork's verifier.</summary>
    public bool PublishWrapper(byte[] wrapper)
    {
        Eip8288Verifier? verifier;
        // A pending upgrade rule publishes nothing; the gossip loop offers the wrapper again on its next cycle.
        try { verifier = _wrappers.RefreshVerifier(); }
        catch (InvalidOperationException) { return false; }
        return verifier is { } current && Publish(wrapper, current);
    }

    /// <summary>EIP-8288 upgrade rule for offered wrappers: drops every mode-1 wrapper and keeps a mode-0 wrapper only if its
    /// direct witnesses re-validate under <paramref name="verifier"/>.</summary>
    private void OnVerifierChanged(Eip8288Verifier verifier)
    {
        List<WrapperStore.Entry> held;
        lock (_gate)
        {
            _verifier = verifier;
            held = [.. _store.All];
        }
        List<WrapperStore.Entry> revalidated = [];
        foreach (WrapperStore.Entry entry in held)
            if (entry.Verifier == verifier || _wrappers.Revalidates(entry.Wrapper, in verifier)) revalidated.Add(entry);
        int dropped = 0;
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed || _verifier != verifier) return;
            foreach (WrapperStore.Entry entry in held)
            {
                if (revalidated.Contains(entry)) entry.Verifier = verifier;
                else if (_store.TryGet(entry.Announcement.Hash, out WrapperStore.Entry current) && current == entry)
                {
                    _store.Remove(entry.Announcement.Hash);
                    dropped++;
                }
            }
            // EIP-8437: announcement and coverage records made under the previous rules no longer suppress anything, and a
            // revalidated wrapper is announced again.
            foreach (WrapperPeer peer in _peers)
            {
                peer.Known.Clear();
                peer.Exchanged.Clear();
                peer.ToAnnounce.Clear();
                foreach (WrapperStore.Entry entry in _store.Recent())
                    if (entry.Verifier == verifier) peer.ToAnnounce.Add(entry.Announcement);
                FlushAnnouncements(peer, outbox);
            }
        }
        outbox.Flush();
        WrapperMetrics.Record(WrapperEvent.UpgradeDropped, dropped);
        if (_logger.IsInfo) _logger.Info($"eth/73 target fork verifier is now {verifier}: dropped {dropped} wrappers, kept {revalidated.Count}");
    }

    /// <summary>Offers a fully validated wrapper with the envelopes it lists, and announces it to peers that may lack it.</summary>
    /// <remarks>The listed envelopes are retained in the bounded wrapper-envelope cache while the wrapper is offered, so they stay
    /// available to GetPooledTransactions after the pool rejects or evicts them.</remarks>
    private bool Publish(byte[] wrapper, Eip8288Verifier verifier, IReadOnlyDictionary<ValueHash256, Transaction>? resolved = null)
    {
        if (!IsEnabled || wrapper.Length is 0 or > Eth73Protocol.MaxWrapperBytes) return false;
        ValueHash256[] hashes;
        WrapperShape shape;
        try { hashes = WrapperBodies.ParseWrapper(wrapper, out shape); }
        catch (RlpException) { return false; }
        Dictionary<ValueHash256, byte[]> envelopes = new(hashes.Length);
        long bytes = wrapper.Length;
        foreach (ValueHash256 hash in hashes)
        {
            byte[]? envelope = null;
            if (resolved?.TryGetValue(hash, out Transaction? transaction) == true || _txPool.TryGetPendingTransaction(hash, out transaction))
                envelope = TxDecoder.Instance.Encode(transaction!, RlpBehaviors.SkipTypedWrapping).Bytes;
            else lock (_gate) if (_store.TryGetEnvelope(hash, out byte[] retained) || _retained.TryGet(hash, out retained)) envelope = retained;
            if (envelope is null || ValueKeccak.Compute(envelope) != hash) return false;
            bytes += envelope.Length;
            if (bytes > WrapperTransportLimits.MaxStoreBytes) return false;
            envelopes[hash] = envelope;
        }
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(wrapper, shape.Mode);
        Outbox outbox = new();
        lock (_gate)
        {
            // A wrapper validated under a verifier the target fork no longer uses is never offered.
            if (_disposed || verifier != _verifier
                || !_store.TryAdd(new(announcement, (byte[])wrapper.Clone(), hashes, envelopes, verifier, shape))) return false;
            _tombstones.Remove(announcement.Hash);
            // Envelopes pushed ahead of this wrapper are now covered, and served from the wrapper-envelope cache.
            foreach (ValueHash256 hash in hashes) _retained.Remove(hash);
            long now = _clock.GetTimestamp();
            foreach (WrapperPeer peer in _peers)
            {
                if (peer.Known.Contains(announcement.Hash, now)) continue;
                peer.ToAnnounce.Add(announcement);
                FlushAnnouncements(peer, outbox);
            }
        }
        outbox.Flush();
        WrapperMetrics.Record(WrapperEvent.Published);
        if (_logger.IsDebug) _logger.Debug($"eth/73 offering {announcement}");
        return true;
    }

    /// <summary>Sends queued announcements within the per-peer rate limit, at most 64 entries per message.</summary>
    /// <remarks>EIP-8437 suppression is applied when an announcement is sent, against what the connection has seen by then: a
    /// hash already announced or sent in either direction, or a wrapper whose transactions the peer already has covered.</remarks>
    private void FlushAnnouncements(WrapperPeer peer, Outbox outbox)
    {
        long now = _clock.GetTimestamp();
        while (peer.ToAnnounce.Count > 0 && peer.AnnouncementsOut.TryTake(1))
        {
            int count = Math.Min(peer.ToAnnounce.Count, Eth73Protocol.MaxAnnouncements);
            List<WrapperAnnouncement> batch = [];
            foreach (WrapperAnnouncement announcement in peer.ToAnnounce.GetRange(0, count))
            {
                if (peer.Known.Contains(announcement.Hash, now) || !TryGetOffered(announcement.Hash, out WrapperStore.Entry entry)
                    || !batch.TrueForAll(d => d.Hash != announcement.Hash)) continue;
                if (IsRedundantFor(peer, entry, now))
                {
                    WrapperMetrics.Record(WrapperEvent.AnnouncementSuppressed);
                    continue;
                }
                batch.Add(announcement);
            }
            peer.ToAnnounce.RemoveRange(0, count);
            if (batch.Count == 0) continue;
            batch.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));
            foreach (WrapperAnnouncement announcement in batch) peer.Known.Add(announcement.Hash);
            outbox.Send(peer, new NewProofWrapperHashesMessage([.. batch]));
            WrapperMetrics.Record(WrapperEvent.Announced, batch.Count);
        }
    }

    /// <summary>Whether wrappers exchanged with <paramref name="peer"/> already cover every transaction of
    /// <paramref name="entry"/> under the same verifier, with no more proof bytes and no more verification work.</summary>
    /// <remarks>
    /// EIP-8437: a wrapper is not announced when its transactions are covered by wrappers sent to or received from the
    /// recipient, or announced by it, unless it reduces proof bytes or verification work. Only held wrappers can be compared,
    /// since an announcement alone does not reveal what a wrapper covers. The cover is chosen greedily, preferring wrappers
    /// that cover more of the remaining transactions and then cheaper ones; verification work prices direct dependencies at
    /// their EIP-8288 verification gas and a recursive STARK at one leanSTARK verification.
    /// </remarks>
    private bool IsRedundantFor(WrapperPeer peer, WrapperStore.Entry entry, long now)
    {
        HashSet<ValueHash256> uncovered = [.. entry.Hashes];
        Dictionary<ValueHash256, WrapperStore.Entry> candidates = [];
        foreach (ValueHash256 transaction in entry.Hashes)
            foreach (ValueHash256 wrapperHash in _store.Listing(transaction))
                if (wrapperHash != entry.Announcement.Hash && !candidates.ContainsKey(wrapperHash)
                    && _store.TryGet(wrapperHash, out WrapperStore.Entry candidate) && candidate.Verifier == entry.Verifier
                    && peer.Exchanged.Contains(wrapperHash, now))
                    candidates[wrapperHash] = candidate;
        if (candidates.Count == 0) return false;
        long proofBytes = 0, work = 0;
        while (uncovered.Count > 0)
        {
            WrapperStore.Entry? best = null;
            int bestCovered = 0;
            foreach (WrapperStore.Entry candidate in candidates.Values)
            {
                int covered = 0;
                foreach (ValueHash256 transaction in candidate.Hashes) if (uncovered.Contains(transaction)) covered++;
                if (covered > bestCovered || covered == bestCovered && covered > 0 && best is not null
                    && candidate.Shape.VerificationWork < best.Shape.VerificationWork)
                {
                    best = candidate;
                    bestCovered = covered;
                }
            }
            if (best is null) return false;
            candidates.Remove(best.Announcement.Hash);
            foreach (ValueHash256 transaction in best.Hashes) uncovered.Remove(transaction);
            proofBytes += best.Shape.ProofBytes;
            work += best.Shape.VerificationWork;
        }
        return entry.Shape.ProofBytes >= proofBytes && entry.Shape.VerificationWork >= work;
    }

    // ---- Responder ---------------------------------------------------------------------------------------------------

    /// <summary>Returns the requested wrappers this node offers, in request order, adding none once the response reaches the
    /// soft response limit; the last one added may take it past the limit (EIP-8437).</summary>
    internal void OnGetProofWrappers(WrapperPeer peer, GetProofWrappersMessage request)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            List<WrapperStore.Entry> served = [];
            int content = 0;
            long now = _clock.GetTimestamp();
            foreach (ValueHash256 wrapperHash in request.Hashes)
            {
                // Requests name wrappers the recipient announced; others are answered as unavailable.
                if (!peer.Known.Contains(wrapperHash, now) || !TryGetOffered(wrapperHash, out WrapperStore.Entry entry)) continue;
                if (ResponseLength(request.RequestId, content) >= Eth73Protocol.SoftResponseLimit) break;
                served.Add(entry);
                content += entry.Wrapper.Length;
            }
            // Beyond the serving budget a responder answers with an empty list.
            if (served.Count > 0 && !peer.Serving.TryTake(content)) served.Clear();
            byte[][] wrappers = new byte[served.Count][];
            for (int i = 0; i < wrappers.Length; i++)
            {
                wrappers[i] = served[i].Wrapper;
                peer.Known.Add(served[i].Announcement.Hash);
                peer.Exchanged.Add(served[i].Announcement.Hash);
            }
            if (wrappers.Length > 0) WrapperMetrics.Record(WrapperEvent.Served, wrappers.Length);
            outbox.Send(peer, new ProofWrappersMessage(request.RequestId, wrappers));
        }
        outbox.Flush();
    }

    /// <summary>Finds an envelope listed in a held wrapper in the wrapper-envelope cache, for a GetPooledTransactions
    /// response; pool lookups are the caller's.</summary>
    /// <remarks>EIP-8437 limits these lookups to the pool and this bounded cache: no historical search or recursive fetch.</remarks>
    internal bool TryGetEnvelope(in ValueHash256 hash, [NotNullWhen(true)] out Transaction? transaction)
    {
        transaction = null;
        byte[]? envelope;
        lock (_gate)
        {
            if (_disposed || !_store.TryGetEnvelope(hash, out envelope)) return false;
        }
        transaction = TryDecode(envelope, hash);
        return transaction is not null;
    }

    /// <summary>The message-data length of <c>[request_id, [items]]</c> whose items encode in <paramref name="itemsLength"/> bytes.</summary>
    internal static int ResponseLength(long requestId, int itemsLength) =>
        Eth73Rlp.LengthOfList(Eth73Rlp.LengthOfUInt((ulong)requestId) + Eth73Rlp.LengthOfList(itemsLength));
}
