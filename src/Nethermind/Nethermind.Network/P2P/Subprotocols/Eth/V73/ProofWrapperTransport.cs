// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>
/// EIP-8437 wrapper propagation shared by every <c>eth/73</c> connection: announcements, whole-object retrieval, envelope
/// recovery, validation hand-off and serving of validated wrappers.
/// </summary>
/// <remarks>
/// All peer, request and fetch state is guarded by one lock; messages and penalties are sent after it is released. No
/// wrapper is announced, served, admitted or reaggregated before it is fully validated under EIP-8288.
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
    private readonly Queue<WrapperFetch> _validation = [];
    private readonly SemaphoreSlim _verification = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private long _chargedBytes;
    private int _validationWorkers;
    private int _nextPeer;
    private bool _disposed;

    public ProofWrapperTransport(ITxPool txPool, ProofWrapperService wrappers, ILogManager logManager, TimeProvider? timeProvider = null)
    {
        _txPool = txPool;
        _wrappers = wrappers;
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logManager.GetClassLogger<ProofWrapperTransport>();
        _wrappers.WrapperValidated += OnWrapperValidated;
        _timer = _clock.CreateTimer(static state => ((ProofWrapperTransport)state!).Tick(), this, WrapperTransportLimits.Tick, WrapperTransportLimits.Tick);
    }

    /// <summary>Whether the head activates EIP-8288; the transport is inert otherwise.</summary>
    public bool IsEnabled => _wrappers.IsEnabled;

    internal int PeerCount { get { lock (_gate) return _peers.Count; } }
    internal int FetchCount { get { lock (_gate) return _fetches.Count; } }
    internal int StoredObjects { get { lock (_gate) return _store.Count; } }
    internal long ChargedBytes { get { lock (_gate) return _chargedBytes; } }
    internal bool IsStored(in ValueHash256 objectId) { lock (_gate) return _store.Contains(objectId); }
    internal bool IsTombstoned(in ValueHash256 objectId) { lock (_gate) return _tombstones.Contains(objectId, _clock.GetTimestamp()); }

    /// <summary>Registers an <c>eth/73</c> peer once its Status is accepted.</summary>
    /// <remarks>The handshake's <c>forkid</c> fixes <c>AGGREGATED_VK</c>, so a peer on <c>eth/73</c> shares this node's verifier.
    /// Null while EIP-8288 is inactive, which keeps the wrapper messages off the connection.</remarks>
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
            // Peers announce the wrappers they offer on connection, within the announcement rate limit.
            long now = _clock.GetTimestamp();
            foreach (WrapperStore.Entry entry in _store.Recent())
            {
                if (peer.ToAnnounce.Count == Eth73Protocol.MaxAnnouncements) break;
                if (!peer.Known.Contains(entry.Descriptor.ObjectId, now)) peer.ToAnnounce.Add(entry.Descriptor);
            }
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
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed) return;
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
        _timer.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Messages, completions and penalties collected under the lock and acted on after it is released.</summary>
    private sealed class Outbox
    {
        private List<WrapperPeer>? _recipients;
        private List<(WrapperPeer Peer, string Reason)>? _penalties;
        private List<TaskCompletionSource<WrapperEnvelopesMessage?>>? _completions;
        private List<CancellationTokenSource>? _cancellations;

        public void Send(WrapperPeer peer, Eth73Message message)
        {
            peer.Outgoing.Enqueue(message);
            if (!(_recipients ??= []).Contains(peer)) _recipients.Add(peer);
        }

        public void Penalize(WrapperPeer peer, string reason) => (_penalties ??= []).Add((peer, reason));

        /// <summary>Ends an envelope request without a response, after the lock is released.</summary>
        public void Complete(TaskCompletionSource<WrapperEnvelopesMessage?> completion) => (_completions ??= []).Add(completion);

        /// <summary>Cancels work after the lock is released, since cancellation callbacks may resume awaiting code inline.</summary>
        public void Cancel(CancellationTokenSource source) => (_cancellations ??= []).Add(source);

        public void Flush()
        {
            foreach (TaskCompletionSource<WrapperEnvelopesMessage?> completion in _completions ?? []) completion.TrySetResult(null);
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
                    while (peer.Outgoing.TryDequeue(out Eth73Message? message))
                        if (!peer.Closed) peer.Link.Send(message);
                }
                finally { Volatile.Write(ref peer.Sending, 0); }
            }
        }
    }

    // ---- Publication -------------------------------------------------------------------------------------------------

    private void OnWrapperValidated(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction> resolved) =>
        _ = Task.Run(() =>
        {
            try { Publish(wrapper, resolved); }
            catch (Exception exception) when (exception is ArgumentException or RlpException or InvalidOperationException)
            {
                if (_logger.IsDebug) _logger.Debug($"eth/73 could not offer a wrapper: {exception.Message}");
            }
        });

    /// <summary>Stores and announces a locally aggregated wrapper.</summary>
    public bool PublishWrapper(byte[] wrapper) => Publish(wrapper);

    /// <summary>Offers a fully validated wrapper with the envelopes it lists, and announces it to peers that may lack it.</summary>
    /// <remarks>A wrapper listing an envelope above the soft response limit is never announced: it could not be served.</remarks>
    private bool Publish(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction>? resolved = null)
    {
        if (!IsEnabled || wrapper.Length is 0 or > Eth73Protocol.MaxWrapperBytes) return false;
        ValueHash256[] hashes;
        try { hashes = WrapperBodies.ParseWrapper(wrapper); }
        catch (RlpException) { return false; }
        Dictionary<ValueHash256, byte[]> envelopes = new(hashes.Length);
        long bytes = wrapper.Length;
        foreach (ValueHash256 hash in hashes)
        {
            byte[]? envelope = null;
            if (resolved?.TryGetValue(hash, out Transaction? transaction) == true || _txPool.TryGetPendingTransaction(hash, out transaction))
                envelope = TxDecoder.Instance.Encode(transaction!, RlpBehaviors.SkipTypedWrapping).Bytes;
            else lock (_gate) if (_store.TryGetEnvelope(hash, out byte[] retained)) envelope = retained;
            if (envelope is null || envelope.Length > Eth73Protocol.SoftResponseLimit || ValueKeccak.Compute(envelope) != hash) return false;
            bytes += envelope.Length;
            if (bytes > WrapperTransportLimits.MaxStoreBytes) return false;
            envelopes[hash] = envelope;
        }
        WrapperDescriptor descriptor = WrapperDescriptor.Create(wrapper);
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed || !_store.TryAdd(new(descriptor, (byte[])wrapper.Clone(), hashes, envelopes))) return false;
            _tombstones.Remove(descriptor.ObjectId);
            long now = _clock.GetTimestamp();
            foreach (WrapperPeer peer in _peers)
            {
                if (peer.Known.Contains(descriptor.ObjectId, now)) continue;
                peer.ToAnnounce.Add(descriptor);
                FlushAnnouncements(peer, outbox);
            }
        }
        outbox.Flush();
        WrapperMetrics.Record(WrapperEvent.Published);
        if (_logger.IsDebug) _logger.Debug($"eth/73 offering {descriptor}");
        return true;
    }

    /// <summary>Sends queued announcements within the per-peer rate limit, at most 64 descriptors per message.</summary>
    private void FlushAnnouncements(WrapperPeer peer, Outbox outbox)
    {
        long now = _clock.GetTimestamp();
        while (peer.ToAnnounce.Count > 0 && peer.AnnouncementsOut.TryTake(1))
        {
            int count = Math.Min(peer.ToAnnounce.Count, Eth73Protocol.MaxAnnouncements);
            List<WrapperDescriptor> batch = [];
            foreach (WrapperDescriptor descriptor in peer.ToAnnounce.GetRange(0, count))
                if (!peer.Known.Contains(descriptor.ObjectId, now) && _store.Contains(descriptor.ObjectId) && batch.TrueForAll(d => d.ObjectId != descriptor.ObjectId))
                    batch.Add(descriptor);
            peer.ToAnnounce.RemoveRange(0, count);
            if (batch.Count == 0) continue;
            batch.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
            foreach (WrapperDescriptor descriptor in batch) peer.Known.Add(descriptor.ObjectId);
            outbox.Send(peer, new AnnounceProofWrappersMessage([.. batch]));
            WrapperMetrics.Record(WrapperEvent.Announced, batch.Count);
        }
    }

    // ---- Responder ---------------------------------------------------------------------------------------------------

    /// <summary>Returns the requested wrappers this node offers, in request order, stopping before the soft response limit.</summary>
    internal void OnGetProofWrappers(WrapperPeer peer, GetProofWrappersMessage request)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            List<WrapperStore.Entry> served = [];
            int content = 0;
            foreach (ValueHash256 objectId in request.ObjectIds)
            {
                if (!_store.TryGet(objectId, out WrapperStore.Entry entry)) continue;
                if (served.Count > 0 && ResponseLength(request.RequestId, content + entry.Wrapper.Length) > Eth73Protocol.SoftResponseLimit) break;
                served.Add(entry);
                content += entry.Wrapper.Length;
            }
            // Beyond the serving budget a responder answers with an empty list.
            if (served.Count > 0 && !peer.Serving.TryTake(content)) served.Clear();
            byte[][] wrappers = new byte[served.Count][];
            for (int i = 0; i < wrappers.Length; i++)
            {
                wrappers[i] = served[i].Wrapper;
                peer.Known.Add(served[i].Descriptor.ObjectId);
            }
            if (wrappers.Length > 0) WrapperMetrics.Record(WrapperEvent.Served, wrappers.Length);
            outbox.Send(peer, new ProofWrappersMessage(request.RequestId, wrappers));
        }
        outbox.Flush();
    }

    /// <summary>Serves a held wrapper's listed envelopes, including ones evicted from the pool; empty for an unknown wrapper.</summary>
    internal void OnGetWrapperEnvelopes(WrapperPeer peer, GetWrapperEnvelopesMessage request)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed) return;
            List<byte[]> envelopes = [];
            if (_store.TryGet(request.ObjectId, out WrapperStore.Entry entry))
            {
                if (request.Indices[^1] >= entry.Hashes.Length)
                {
                    Violation(peer, $"envelope index {request.Indices[^1]} is not below {entry.Hashes.Length}", outbox);
                    outbox.Flush();
                    return;
                }
                int content = 0;
                foreach (int index in request.Indices)
                {
                    byte[] envelope = entry.Envelopes[entry.Hashes[index]];
                    int length = Eth73Rlp.LengthOfBytes(envelope);
                    if (envelopes.Count > 0 && ResponseLength(request.RequestId, content + length) > Eth73Protocol.SoftResponseLimit) break;
                    envelopes.Add(envelope);
                    content += length;
                }
                if (!peer.Serving.TryTake(content)) envelopes.Clear();
            }
            outbox.Send(peer, new WrapperEnvelopesMessage(request.RequestId, [.. envelopes]));
        }
        outbox.Flush();
    }

    /// <summary>The message-data length of <c>[request_id, [items]]</c> whose items encode in <paramref name="itemsLength"/> bytes.</summary>
    internal static int ResponseLength(long requestId, int itemsLength) =>
        Eth73Rlp.LengthOfList(Eth73Rlp.LengthOfUInt((ulong)requestId) + Eth73Rlp.LengthOfList(itemsLength));
}
