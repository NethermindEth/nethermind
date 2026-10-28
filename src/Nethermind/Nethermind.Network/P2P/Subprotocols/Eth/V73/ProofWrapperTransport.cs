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
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// EIP-8437 wrapper propagation shared by every <c>eth/73</c> connection: announcements, whole-wrapper retrieval, validation
/// hand-off, and serving of validated wrappers.
/// </summary>
/// <remarks>
/// All peer, request and fetch state is guarded by one lock; messages and penalties are sent after it is released. No
/// wrapper is announced, served, admitted or reaggregated before it is fully validated under EIP-8288, and none is announced
/// or served once the target fork's verifier differs from the one it was validated under: on a verifier change every
/// mode-1 wrapper is dropped and a mode-0 wrapper is kept only if it re-validates.
/// </remarks>
public sealed partial class ProofWrapperTransport : IDisposable
{
    private readonly ProofWrapperService _wrappers;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<WrapperPeer> _peers = [];
    private readonly WrapperStore _store = new();
    private readonly WrapperEncodingBudget _encodingBudget = new(WrapperTransportLimits.MaxCachedEncodingBytes);
    private readonly Dictionary<ValueHash256, WrapperFetch> _fetches = [];
    private readonly WrapperBoundedSet _tombstones = new(WrapperTransportLimits.MaxTombstones);
    // Wrappers that failed proof or dependency rules, keyed by InvalidKey with the verifier they were judged under: another
    // target fork verifier may accept them.
    private readonly WrapperBoundedSet _invalid = new(WrapperTransportLimits.MaxTombstones);
    private readonly Queue<WrapperFetch> _validation = [];
    private readonly SemaphoreSlim _verification = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer? _timer;
    private long _chargedBytes;
    private int _validationWorkers;
    private int _nextPeer;
    private bool _disposed;
    private Eip8288Verifier? _verifier;

    public ProofWrapperTransport(ProofWrapperService wrappers, ILogManager logManager, TimeProvider? timeProvider = null)
    {
        _wrappers = wrappers;
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logManager.GetClassLogger<ProofWrapperTransport>();
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
    internal long CachedEncodingBytes => _encodingBudget.Used;
    internal bool IsStored(in ValueHash256 wrapperHash) { lock (_gate) return _store.Contains(wrapperHash); }
    internal bool IsOffered(in ValueHash256 wrapperHash) { lock (_gate) return TryGetOffered(wrapperHash, out _); }

    /// <summary>A held wrapper validated under the current target fork's verifier, which alone may be announced or served.</summary>
    private bool TryGetOffered(in ValueHash256 wrapperHash, out WrapperStore.Entry entry)
        => _store.TryGet(wrapperHash, out entry) && entry.Verifier == _verifier;
    internal bool IsTombstoned(in ValueHash256 wrapperHash) { lock (_gate) return IsTombstoned(wrapperHash, _clock.GetTimestamp()); }

    private bool IsTombstoned(in ValueHash256 wrapperHash, long now) => _tombstones.Contains(wrapperHash, now)
        || _wrappers.CurrentVerifier is { } verifier && _invalid.Contains(InvalidKey(wrapperHash, verifier), now);

    private static ValueHash256 InvalidKey(in ValueHash256 wrapperHash, in Eip8288Verifier verifier)
    {
        Span<byte> key = stackalloc byte[2 * ValueHash256.MemorySize];
        wrapperHash.Bytes.CopyTo(key);
        verifier.AggregatedVk.Bytes.CopyTo(key[ValueHash256.MemorySize..]);
        return ValueKeccak.Compute(key);
    }

    /// <summary>Registers an <c>eth/73</c> peer once its Status is accepted.</summary>
    /// <remarks>Peers negotiate no verifier: each validates under its own target fork. A peer connected before EIP-8288 is
    /// enabled is registered too, but no wrapper message is exchanged with it until then. Null on a chain without a scheduled
    /// EIP-8288 fork.</remarks>
    internal WrapperPeer? Accept(IWrapperLink link)
    {
        if (!_wrappers.IsScheduled) return null;
        Outbox outbox = new();
        WrapperPeer peer;
        lock (_gate)
        {
            if (_disposed) return null;
            peer = new(link, _clock, _encodingBudget);
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
        foreach (WrappersRequest request in peer.Requests.Values) Abandon(peer, request);
        peer.Requests.Clear();
        peer.ToAnnounce.Clear();
        peer.Received.Close();
        foreach (WrapperFetch fetch in _fetches.Values) fetch.Sources.Remove(peer);
    }

    /// <summary>Releases an outstanding request's reservation; its wanted wrappers go back to the queue.</summary>
    private void Abandon(WrapperPeer peer, WrappersRequest request)
    {
        Release(peer, request.Reserved);
        foreach (WrapperFetch fetch in request.Fetches) fetch.Requested = false;
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

    /// <summary>Malformed transport data or a protocol violation: the contribution is discarded and the peer disconnected.</summary>
    private void Violation(WrapperPeer peer, string reason, Outbox outbox)
    {
        WrapperMetrics.Record(WrapperEvent.Violation);
        if (_logger.IsDebug) _logger.Debug($"eth/73 wrapper violation by {peer}: {reason}");
        RemoveCore(peer, outbox);
        outbox.Penalize(peer, $"eth/73: {reason}");
    }

    /// <summary>Resolves a response's request; null when it is discarded or the peer was penalized.</summary>
    private WrappersRequest? TakeRequest(WrapperPeer peer, long requestId, Outbox outbox)
    {
        if (peer.Requests.Remove(requestId, out WrappersRequest? request)) return request;
        // An issued request that expired is discarded; one never issued is a violation.
        if (requestId <= 0 || requestId > peer.LastIssued) Violation(peer, $"response to unissued request {requestId}", outbox);
        return null;
    }

    private void Tick()
    {
        // Applies the local verifier-change rule, through VerifierChanged, once the head reaches a fork with another verifier.
        // Before activation it runs only for peers, which register once the fork is scheduled.
        if (!IsEnabled && PeerCount == 0) return;
        Eip8288Verifier? refreshed;
        try { refreshed = _wrappers.RefreshVerifier(); }
        catch (Exception exception)
        {
            // An exception escaping a timer callback ends the process; the verifier-change rule is retried on the next tick.
            if (_logger.IsError) _logger.Error("eth/73 could not apply the local verifier-change rule", exception);
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
                List<WrappersRequest>? expired = null;
                foreach (WrappersRequest request in peer.Requests.Values)
                    if (_clock.GetElapsedTime(request.Created, now) >= WrapperTransportLimits.RequestTimeout) (expired ??= []).Add(request);
                foreach (WrappersRequest request in expired ?? [])
                {
                    peer.Requests.Remove(request.Id);
                    WrapperMetrics.Record(WrapperEvent.RequestExpired);
                    Abandon(peer, request);
                }
                FlushAnnouncements(peer, outbox);
            }
            ExpireFetches(now, outbox);
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
            // Workers stop at the next dequeue, so queued validations are released here; running ones release their own.
            while (_validation.TryDequeue(out WrapperFetch? queued))
            {
                queued.Processing?.Dispose();
                queued.Processing = null;
            }
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

    /// <summary>Messages, cancellations and penalties collected under the lock and acted on after it is released.</summary>
    private sealed class Outbox
    {
        private List<WrapperPeer>? _recipients;
        private List<(WrapperPeer Peer, string Reason)>? _penalties;
        private List<CancellationTokenSource>? _cancellations;

        public void Send(WrapperPeer peer, P2PMessage message)
        {
            peer.Outgoing.Enqueue(message);
            if (!(_recipients ??= []).Contains(peer)) _recipients.Add(peer);
        }

        public void Penalize(WrapperPeer peer, string reason) => (_penalties ??= []).Add((peer, reason));

        /// <summary>Cancels work after the lock is released, since cancellation callbacks may resume awaiting code inline.</summary>
        public void Cancel(CancellationTokenSource source) => (_cancellations ??= []).Add(source);

        public void Flush()
        {
            foreach (CancellationTokenSource source in _cancellations ?? [])
            {
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            foreach (WrapperPeer peer in _recipients ?? []) Drain(peer);
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

    private void OnWrapperValidated(byte[] wrapper, Eip8288Verifier verifier) =>
        _ = Task.Run(() =>
        {
            try { Publish(wrapper, verifier); }
            catch (Exception exception) when (exception is ArgumentException or RlpException or InvalidOperationException)
            {
                if (_logger.IsDebug) _logger.Debug($"eth/73 could not offer a wrapper: {exception.Message}");
            }
        });

    /// <summary>Stores and announces a locally aggregated wrapper, produced under the current target fork's verifier.</summary>
    public bool PublishWrapper(byte[] wrapper)
    {
        Eip8288Verifier? verifier;
        // A pending verifier-change rule publishes nothing; the gossip loop offers the wrapper again on its next cycle.
        try { verifier = _wrappers.RefreshVerifier(); }
        catch (InvalidOperationException) { return false; }
        return verifier is { } current && Publish(wrapper, current);
    }

    /// <summary>Local verifier-change rule for offered wrappers: drops every mode-1 wrapper and keeps a mode-0 wrapper only if its
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
            // EIP-8437: announcement and coverage records made under the previous rules no longer suppress or authorize
            // serving anything, and a revalidated wrapper is announced again.
            foreach (WrapperPeer peer in _peers)
            {
                peer.Known.Clear();
                peer.Announced.Clear();
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

    /// <summary>Offers a fully validated wrapper and announces it to peers that may lack it.</summary>
    private bool Publish(byte[] wrapper, Eip8288Verifier verifier)
    {
        if (!IsEnabled || wrapper.Length is 0 or > Eth73Protocol.MaxWrapperBytes) return false;
        ValueHash256[] hashes;
        WrapperShape shape;
        try { hashes = WrapperBodies.ParseWrapper(wrapper, out shape); }
        catch (RlpException) { return false; }
        ValueHash256[] encodingHashes = WrapperCompaction.EncodingHashes(wrapper);
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(wrapper, shape.Mode);
        Outbox outbox = new();
        lock (_gate)
        {
            // A wrapper validated under a verifier the target fork no longer uses is never offered.
            if (_disposed || verifier != _verifier
                || !_store.TryAdd(new(announcement, (byte[])wrapper.Clone(), hashes, verifier, shape, encodingHashes))) return false;
            _tombstones.Remove(announcement.Hash);
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
        if (_verifier is null) return;
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
            foreach (WrapperAnnouncement announcement in batch)
            {
                peer.Known.Add(announcement.Hash);
                peer.Announced.Add(announcement.Hash);
            }
            outbox.Send(peer, new NewProofWrapperHashesMessage([.. batch]));
            WrapperMetrics.Record(WrapperEvent.Announced, batch.Count);
        }
    }

    /// <summary>Whether wrappers exchanged with <paramref name="peer"/> already cover every transaction of
    /// <paramref name="entry"/> under the same verifier, with no more proof bytes and no more verification work.</summary>
    /// <remarks>
    /// EIP-8437: a wrapper is not announced when its transactions are covered by held, validated wrappers exchanged with the
    /// recipient or announced by it, unless it reduces proof bytes or verification work, or provides an IL-bounded mode-1
    /// proof where existing coverage cannot. Only held wrappers can be compared, since an announcement alone does not reveal
    /// what a wrapper covers. The cover is chosen greedily, preferring wrappers that cover more of the remaining transactions
    /// and then cheaper ones; verification work prices direct dependencies at their EIP-8288 verification gas and a recursive
    /// STARK at one leanSTARK verification. A mode-1 wrapper is a single proof within the wrapper dependency limits, which an
    /// inclusion list can reuse whole; it is only redundant when one held mode-1 wrapper already covers all its transactions.
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
        if (entry.Shape.Mode == MempoolWrapper.ModeRecursive && !HasSingleRecursiveCover(entry, candidates.Values)) return false;
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

    private static bool HasSingleRecursiveCover(WrapperStore.Entry entry, IEnumerable<WrapperStore.Entry> candidates)
    {
        foreach (WrapperStore.Entry candidate in candidates)
        {
            if (candidate.Shape.Mode != MempoolWrapper.ModeRecursive) continue;
            HashSet<ValueHash256> covered = [.. candidate.Hashes];
            if (Array.TrueForAll(entry.Hashes, covered.Contains)) return true;
        }
        return false;
    }

    // ---- Responder ---------------------------------------------------------------------------------------------------

    /// <summary>Returns the requested wrappers this node offers and announced to <paramref name="peer"/>, in request order,
    /// adding none once the response reaches the soft response limit; the last one added may take it past the limit
    /// (EIP-8437).</summary>
    /// <remarks>With <c>compact = 1</c>, each transaction encoding this node already sent the peer in full within a validated
    /// wrapper is replaced by its hash reference where that is smaller; with <c>compact = 0</c> wrappers are complete. The
    /// response is sized and charged to the serving budget under the lock from item headers alone; compact wrappers are built
    /// after it is released.</remarks>
    internal void OnGetProofWrappers(WrapperPeer peer, GetProofWrappersMessage request)
    {
        Outbox outbox = new();
        List<(WrapperStore.Entry Entry, bool[]? Replace)> served = [];
        lock (_gate)
        {
            if (peer.Closed) return;
            int content = 0;
            long now = _clock.GetTimestamp();
            foreach (ValueHash256 wrapperHash in request.Hashes)
            {
                peer.Known.Add(wrapperHash);
                // Hashes this node has not announced on this connection are unavailable.
                if (!peer.Announced.Contains(wrapperHash, now) || !TryGetOffered(wrapperHash, out WrapperStore.Entry entry)) continue;
                if (ResponseLength(request.RequestId, content) >= Eth73Protocol.SoftResponseLimit) break;
                bool[]? replace = request.Compact ? Replacements(peer, entry, now) : null;
                served.Add((entry, replace));
                content += EntryLength(replace is null ? entry.Wrapper.Length : WrapperCompaction.CompactLength(entry.Wrapper, replace));
            }
            // Beyond the serving budget a responder answers with an empty list.
            if (served.Count > 0 && !peer.Serving.TryTake(content)) served.Clear();
            foreach ((WrapperStore.Entry entry, _) in served)
            {
                peer.Exchanged.Add(entry.Announcement.Hash);
                // Every encoding of the wrapper is now held by the peer, whether sent now or earlier.
                foreach (ValueHash256 encoding in entry.EncodingHashes) peer.Delivered.Add(encoding);
            }
        }
        ProofWrapperEntry[] entries = new ProofWrapperEntry[served.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            (WrapperStore.Entry entry, bool[]? replace) = served[i];
            entries[i] = new(entry.Announcement.Hash, replace is null ? entry.Wrapper : WrapperCompaction.Compact(entry.Wrapper, replace, entry.EncodingHashes));
        }
        if (entries.Length > 0) WrapperMetrics.Record(WrapperEvent.Served, entries.Length);
        outbox.Send(peer, new ProofWrappersMessage(request.RequestId, entries));
        outbox.Flush();
    }

    /// <summary>Per transaction of <paramref name="entry"/>, whether its encoding was previously sent to <paramref name="peer"/>
    /// in full; null when none was.</summary>
    private static bool[]? Replacements(WrapperPeer peer, WrapperStore.Entry entry, long now)
    {
        bool[]? replace = null;
        int references = 0;
        for (int i = 0; i < entry.EncodingHashes.Length; i++)
        {
            if (!peer.Delivered.Contains(entry.EncodingHashes[i], now)) continue;
            (replace ??= new bool[entry.EncodingHashes.Length])[i] = true;
            references++;
        }
        if (references > 0) WrapperMetrics.Record(WrapperEvent.ReferenceSent, references);
        return replace;
    }

    /// <summary>The encoded length of a ProofWrappers entry <c>[hash, wrapper]</c> for a wrapper of <paramref name="wrapperLength"/> bytes.</summary>
    internal static int EntryLength(int wrapperLength) => Eth73Rlp.LengthOfList(1 + ValueHash256.MemorySize + wrapperLength);

    /// <summary>The message-data length of <c>[request_id, [items]]</c> whose items encode in <paramref name="itemsLength"/> bytes.</summary>
    internal static int ResponseLength(long requestId, int itemsLength) =>
        Eth73Rlp.LengthOfList(Eth73Rlp.LengthOfUInt((ulong)requestId) + Eth73Rlp.LengthOfList(itemsLength));
}
