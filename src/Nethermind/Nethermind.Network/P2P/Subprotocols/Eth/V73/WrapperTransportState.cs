// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>The connection side of an <c>eth/73</c> peer.</summary>
internal interface IWrapperLink
{
    string Description { get; }

    /// <summary>Sends a wrapper message or an envelope-recovery GetPooledTransactions.</summary>
    void Send(P2PMessage message);

    /// <summary>Disconnects the peer for invalid peer data or a protocol violation.</summary>
    void Penalize(string reason);

    /// <summary>Lowers the peer's transaction-gossip score without disconnecting it.</summary>
    void LowerScore(string reason);

    /// <summary>Whether a returned transaction agrees with the type and size the peer announced for it through
    /// NewPooledTransactionHashes; true when it announced none (EIP-5793).</summary>
    bool MatchesAnnouncedShape(Transaction transaction);
}

/// <summary>Bounded LRU set, optionally with expiring entries, for known-object sets and tombstones.</summary>
internal sealed class WrapperBoundedSet(int capacity)
{
    private readonly Dictionary<ValueHash256, LinkedListNode<(ValueHash256 Key, long Expiry)>> _items = [];
    private readonly LinkedList<(ValueHash256 Key, long Expiry)> _order = new();

    public int Count => _items.Count;

    public void Add(in ValueHash256 key, long expiry = long.MaxValue)
    {
        if (_items.Remove(key, out LinkedListNode<(ValueHash256, long)>? existing)) _order.Remove(existing);
        else if (_items.Count == capacity)
        {
            _items.Remove(_order.First!.Value.Key);
            _order.RemoveFirst();
        }
        _items[key] = _order.AddLast((key, expiry));
    }

    public bool Contains(in ValueHash256 key, long now)
    {
        if (!_items.TryGetValue(key, out LinkedListNode<(ValueHash256 Key, long Expiry)>? node)) return false;
        if (node.Value.Expiry > now) return true;
        _items.Remove(key);
        _order.Remove(node);
        return false;
    }

    public void Remove(in ValueHash256 key)
    {
        if (_items.Remove(key, out LinkedListNode<(ValueHash256, long)>? node)) _order.Remove(node);
    }

    public void Clear()
    {
        _items.Clear();
        _order.Clear();
    }
}

/// <summary>A refilling allowance, such as a serving or announcement rate limit.</summary>
internal sealed class WrapperRateLimit(double perSecond, TimeProvider clock)
{
    private readonly double _rate = perSecond;
    private double _available = perSecond;
    private long _updated = clock.GetTimestamp();

    /// <summary>Takes <paramref name="amount"/> if available; the allowance refills at its rate up to one second's worth.</summary>
    public bool TryTake(double amount)
    {
        long now = clock.GetTimestamp();
        _available = Math.Min(_rate, _available + clock.GetElapsedTime(_updated, now).TotalSeconds * _rate);
        _updated = now;
        if (amount > _available) return false;
        _available -= amount;
        return true;
    }
}

/// <summary>Fully validated wrappers this node offers, with the envelopes it must serve for them, bounded by bytes and count.</summary>
/// <remarks>Only wrappers that passed full EIP-8288 validation, or were produced locally, are added.</remarks>
internal sealed class WrapperStore(long maxBytes = WrapperTransportLimits.MaxStoreBytes, int maxObjects = WrapperTransportLimits.MaxStoreObjects)
{
    internal sealed class Entry(WrapperAnnouncement announcement, byte[] wrapper, ValueHash256[] hashes, Dictionary<ValueHash256, byte[]> envelopes,
        Eip8288Verifier verifier, WrapperShape shape)
    {
        public WrapperAnnouncement Announcement { get; } = announcement;

        /// <summary>Mode, proof bytes and verification work, compared when suppressing redundant announcements.</summary>
        public WrapperShape Shape { get; } = shape;

        /// <summary>The verifier the wrapper was validated under; it is offered only while that is the target fork's.</summary>
        public Eip8288Verifier Verifier { get; set; } = verifier;
        public byte[] Wrapper { get; } = wrapper;

        /// <summary>The wrapper's <c>transactions</c>, in order.</summary>
        public ValueHash256[] Hashes { get; } = hashes;

        /// <summary>Canonical EIP-2718 envelopes for every listed hash, served even after pool eviction.</summary>
        public Dictionary<ValueHash256, byte[]> Envelopes { get; } = envelopes;
        public LinkedListNode<ValueHash256>? Node { get; set; }
        public long Bytes { get; } = wrapper.Length + 256 + EnvelopeBytes(envelopes);

        private static long EnvelopeBytes(Dictionary<ValueHash256, byte[]> envelopes)
        {
            long bytes = 0;
            foreach (byte[] envelope in envelopes.Values) bytes += envelope.Length + 64;
            return bytes;
        }
    }

    private readonly Dictionary<ValueHash256, Entry> _objects = [];
    private readonly Dictionary<ValueHash256, ValueHash256> _byEnvelope = [];
    private readonly Dictionary<ValueHash256, List<ValueHash256>> _byTransaction = [];
    private readonly LinkedList<ValueHash256> _order = new();

    public long Bytes { get; private set; }
    public int Count => _objects.Count;

    public bool TryGet(in ValueHash256 wrapperHash, out Entry entry) => _objects.TryGetValue(wrapperHash, out entry!);

    public bool Contains(in ValueHash256 wrapperHash) => _objects.ContainsKey(wrapperHash);

    /// <summary>Finds an envelope retained for a hash entry of a stored wrapper.</summary>
    public bool TryGetEnvelope(in ValueHash256 transaction, out byte[] envelope)
    {
        envelope = null!;
        return _byEnvelope.TryGetValue(transaction, out ValueHash256 wrapperHash) && _objects.TryGetValue(wrapperHash, out Entry? entry)
            && entry.Envelopes.TryGetValue(transaction, out envelope!);
    }

    public bool TryAdd(Entry entry)
    {
        ValueHash256 wrapperHash = entry.Announcement.Hash;
        if (_objects.ContainsKey(wrapperHash) || entry.Bytes > maxBytes) return false;
        while (_objects.Count >= maxObjects || entry.Bytes > maxBytes - Bytes) Remove(_order.First!.Value);
        entry.Node = _order.AddLast(wrapperHash);
        _objects[wrapperHash] = entry;
        Bytes += entry.Bytes;
        foreach (ValueHash256 transaction in entry.Envelopes.Keys) _byEnvelope[transaction] = wrapperHash;
        foreach (ValueHash256 transaction in entry.Hashes)
        {
            if (!_byTransaction.TryGetValue(transaction, out List<ValueHash256>? holders)) _byTransaction[transaction] = holders = [];
            holders.Add(wrapperHash);
        }
        return true;
    }

    /// <summary>The held wrappers listing <paramref name="transaction"/>.</summary>
    public IReadOnlyList<ValueHash256> Listing(in ValueHash256 transaction) =>
        _byTransaction.TryGetValue(transaction, out List<ValueHash256>? holders) ? holders : [];

    /// <summary>Every held wrapper.</summary>
    public IReadOnlyCollection<Entry> All => _objects.Values;

    /// <summary>Most recently added wrappers first.</summary>
    public IEnumerable<Entry> Recent()
    {
        for (LinkedListNode<ValueHash256>? node = _order.Last; node is not null; node = node.Previous)
            yield return _objects[node.Value];
    }

    public void Remove(ValueHash256 wrapperHash)
    {
        if (!_objects.Remove(wrapperHash, out Entry? entry)) return;
        _order.Remove(entry.Node!);
        Bytes -= entry.Bytes;
        foreach (ValueHash256 transaction in entry.Envelopes.Keys)
            if (_byEnvelope.TryGetValue(transaction, out ValueHash256 current) && current == wrapperHash) _byEnvelope.Remove(transaction);
        foreach (ValueHash256 transaction in entry.Hashes)
            if (_byTransaction.TryGetValue(transaction, out List<ValueHash256>? holders) && holders.Remove(wrapperHash) && holders.Count == 0)
                _byTransaction.Remove(transaction);
    }
}

/// <summary>A peer's request, budget and suppression state, guarded by the transport lock.</summary>
internal sealed class WrapperPeer(IWrapperLink link, TimeProvider clock)
{
    public IWrapperLink Link { get; } = link;
    public bool Closed { get; set; }

    /// <summary>Object IDs the peer is known to hold, having announced or sent them, or having been told of them.</summary>
    public WrapperBoundedSet Known { get; } = new(WrapperTransportLimits.MaxKnownPerPeer);

    /// <summary>Wrappers sent to or received from this peer, or announced by it: the coverage it already has, against which
    /// a redundant announcement is suppressed. Unlike <see cref="Known"/>, wrappers this node merely announced are excluded.</summary>
    public WrapperBoundedSet Exchanged { get; } = new(WrapperTransportLimits.MaxKnownPerPeer);

    /// <summary>When a local limit last refused this peer's announcements, wrappers or envelopes, or one of its announcements ended
    /// unresolved; envelopes it pushed before then lower no score if they expire uncovered.</summary>
    public long ScoreExemptAt { get; set; } = long.MinValue;

    /// <summary>Envelopes this peer pushed that await a covering wrapper, held to its share of the retention cache.</summary>
    public int RetainedEnvelopes { get; set; }

    public long LastIssued { get; set; }

    /// <summary>Pending fetches this peer's announcements created, held to its share of the pending cache.</summary>
    public int PendingFetches { get; set; }

    /// <summary>Outstanding GetProofWrappers and envelope-recovery GetPooledTransactions requests to this peer.</summary>
    public Dictionary<long, WrapperRequest> Requests { get; } = [];

    /// <summary>Bytes charged to this peer: reserved responses, received wrappers and recovered envelopes.</summary>
    public long ChargedBytes { get; set; }

    public WrapperRateLimit Serving { get; } = new(WrapperTransportLimits.ServeBytesPerSecond, clock);
    public WrapperRateLimit AnnouncementsIn { get; } = new(WrapperTransportLimits.AnnouncementsPerSecond, clock);
    public WrapperRateLimit AnnouncementsOut { get; } = new(WrapperTransportLimits.AnnouncementsPerSecond, clock);

    /// <summary>Announcements awaiting announcement under the rate limit.</summary>
    public List<WrapperAnnouncement> ToAnnounce { get; } = [];

    /// <summary>Messages queued under the transport lock, so request IDs leave in order.</summary>
    public ConcurrentQueue<P2PMessage> Outgoing { get; } = new();

    /// <summary>1 while one thread sends <see cref="Outgoing"/>.</summary>
    public int Sending;

    public bool CanRequest => !Closed && Requests.Count < WrapperTransportLimits.MaxRequestsPerPeer;

    public override string ToString() => Link.Description;
}

internal abstract class WrapperRequest(long id, long created, long reserved)
{
    public long Id { get; } = id;
    public long Created { get; } = created;

    /// <summary>Response bytes reserved against the peer and global budgets until the response arrives.</summary>
    public long Reserved { get; } = reserved;
}

/// <summary>An outstanding GetProofWrappers request; its wanted wrappers in request order.</summary>
internal sealed class WrappersRequest(long id, long created, long reserved, WrapperFetch[] fetches) : WrapperRequest(id, created, reserved)
{
    public WrapperFetch[] Fetches { get; } = fetches;
}

/// <summary>An outstanding envelope-recovery GetPooledTransactions request, awaited by the wrapper's validation.</summary>
internal sealed class EnvelopesRequest(long id, long created, long reserved, ValueHash256[] hashes) : WrapperRequest(id, created, reserved)
{
    /// <summary>The distinct requested hashes, in request order.</summary>
    public ValueHash256[] Hashes { get; } = hashes;

    /// <summary>The matched returned transactions, or null when the request ended without a usable response.</summary>
    public TaskCompletionSource<Transaction[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A wanted wrapper from its first announcement until it is validated or abandoned.</summary>
internal sealed class WrapperFetch(WrapperAnnouncement announcement, long now)
{
    public WrapperAnnouncement Announcement { get; } = announcement;
    public long Announced { get; } = now;

    /// <summary>The peer whose announcement created the fetch, charged with its share of the pending cache.</summary>
    public WrapperPeer? Announcer { get; init; }

    /// <summary>When the first request was sent; the recovery deadline runs from here.</summary>
    public long? Started { get; set; }

    /// <summary>Peers that announced or served the wrapper, up to <see cref="WrapperTransportLimits.MaxSourcesPerWrapper"/>.</summary>
    public List<WrapperPeer> Sources { get; } = [];
    public HashSet<WrapperPeer> Tried { get; } = [];
    public bool Requested { get; set; }

    /// <summary>The peer that served the body, and the first one asked for its envelopes.</summary>
    public WrapperPeer? Server { get; set; }
    public byte[]? Wrapper { get; set; }
    public long ChargedBytes { get; set; }
    public bool Removed { get; set; }

    /// <summary>Cancels validation and envelope recovery at the recovery deadline or shutdown.</summary>
    public CancellationTokenSource? Processing { get; set; }

    /// <summary>The mode and length each source announced; a response is held to its own server's metadata, so one peer's
    /// false metadata cannot make an honest server's response look mismatched.</summary>
    private readonly Dictionary<WrapperPeer, (int Mode, int Length)> _metadata = [];

    public void AddSource(WrapperPeer peer, int mode, int length)
    {
        if (Sources.Contains(peer) || Sources.Count >= WrapperTransportLimits.MaxSourcesPerWrapper) return;
        Sources.Add(peer);
        _metadata[peer] = (mode, length);
    }

    /// <summary>The length <paramref name="peer"/> announced, or the first announcement's.</summary>
    public int LengthFrom(WrapperPeer peer) => _metadata.TryGetValue(peer, out (int Mode, int Length) metadata) ? metadata.Length : Announcement.Length;

    /// <summary>The mode <paramref name="peer"/> announced, or the first announcement's.</summary>
    public int ModeFrom(WrapperPeer peer) => _metadata.TryGetValue(peer, out (int Mode, int Length) metadata) ? metadata.Mode : Announcement.Mode;

    /// <summary>The next peer to ask for the body, or null once every source was tried or the source budget is spent.</summary>
    public WrapperPeer? NextSource()
    {
        if (Tried.Count >= WrapperTransportLimits.MaxSourcesPerWrapper) return null;
        foreach (WrapperPeer peer in Sources)
            if (!peer.Closed && !Tried.Contains(peer)) return peer;
        return null;
    }
}

/// <summary>Dependency-bearing envelopes received through transaction messages before a covering wrapper, within finite byte,
/// count and time limits (EIP-8437). They are never admitted or relayed on their own; a wrapper's validation uses them in place
/// of GetPooledTransactions.</summary>
internal sealed class RetainedEnvelopes(long maxBytes = WrapperTransportLimits.MaxRetainedEnvelopeBytes, int maxCount = WrapperTransportLimits.MaxRetainedEnvelopes)
{
    internal sealed class Item(ValueHash256 hash, byte[] envelope, WrapperPeer from, long received)
    {
        public ValueHash256 Hash { get; } = hash;
        public byte[] Envelope { get; } = envelope;

        /// <summary>The peer that pushed the envelope, scored if no covering wrapper follows on that connection.</summary>
        public WrapperPeer From { get; } = from;
        public long Received { get; } = received;
        public LinkedListNode<Item>? Node { get; set; }
    }

    private readonly Dictionary<ValueHash256, Item> _items = [];
    private readonly LinkedList<Item> _order = new();

    public long Bytes { get; private set; }
    public int Count => _items.Count;

    public bool Contains(in ValueHash256 hash) => _items.ContainsKey(hash);

    public bool TryGet(in ValueHash256 hash, out byte[] envelope)
    {
        envelope = null!;
        if (!_items.TryGetValue(hash, out Item? item)) return false;
        envelope = item.Envelope;
        return true;
    }

    /// <summary>Adds an envelope unless the cache is full; retained envelopes are not evicted early, since each one's expiry
    /// may score its sender.</summary>
    public bool TryAdd(Item item)
    {
        if (_items.ContainsKey(item.Hash) || _items.Count >= maxCount || item.Envelope.Length > maxBytes - Bytes) return false;
        item.Node = _order.AddLast(item);
        _items[item.Hash] = item;
        Bytes += item.Envelope.Length;
        item.From.RetainedEnvelopes++;
        return true;
    }

    public Item? Remove(in ValueHash256 hash)
    {
        if (!_items.Remove(hash, out Item? item)) return null;
        _order.Remove(item.Node!);
        Bytes -= item.Envelope.Length;
        item.From.RetainedEnvelopes--;
        return item;
    }

    /// <summary>Removes and returns the envelopes received at or before <paramref name="cutoff"/>, oldest first.</summary>
    public List<Item>? RemoveReceivedBy(long cutoff)
    {
        List<Item>? expired = null;
        while (_order.First is { } node && node.Value.Received <= cutoff)
        {
            (expired ??= []).Add(node.Value);
            Remove(node.Value.Hash);
        }
        return expired;
    }

    /// <summary>Forgets a disconnected peer's envelopes: a closed connection can no longer announce coverage.</summary>
    public void RemoveFrom(WrapperPeer peer)
    {
        List<ValueHash256>? hashes = null;
        foreach (Item item in _items.Values)
            if (item.From == peer) (hashes ??= []).Add(item.Hash);
        foreach (ValueHash256 hash in hashes ?? []) Remove(hash);
    }
}
