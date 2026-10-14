// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>The connection side of a negotiated <c>lean/1</c> peer.</summary>
internal interface ILeanLink
{
    string Description { get; }
    PublicKey NodeKey { get; }
    bool HasTransactionRecovery { get; }

    /// <summary>Queues a control message.</summary>
    void Send(LeanMessage message);

    /// <summary>Writes one Chunk after backpressure clears; false when the connection can no longer carry it.</summary>
    ValueTask<bool> SendChunkAsync(ChunkMessage message, CancellationToken cancellationToken);

    /// <summary>Stops the transfer and disconnects the peer for invalid data or a protocol violation.</summary>
    void Penalize(string reason);

    Task<Transaction[]> RecoverTransactionsAsync(ValueHash256[] hashes, Func<int, bool> reserve, CancellationToken cancellationToken);
}

/// <summary>Bounded LRU set used for per-peer announcement suppression and tombstones.</summary>
internal sealed class LeanBoundedSet(int capacity)
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
}

/// <summary>Fully validated objects this node serves, bounded by bytes and count.</summary>
/// <remarks>Only objects that passed reconstruction and kind validation, or were produced locally, are added.</remarks>
internal sealed class LeanObjectStore(long maxBytes = LeanLimits.MaxStoreBytes, int maxObjects = LeanLimits.MaxStoreObjects)
{
    internal sealed class Entry(LeanDescriptor descriptor, byte[] body, Dictionary<ValueHash256, Transaction>? envelopes = null)
    {
        public LeanDescriptor Descriptor { get; } = descriptor;
        public byte[] Body { get; } = body;
        public Dictionary<ValueHash256, Transaction>? Envelopes { get; } = envelopes;
        public LinkedListNode<ValueHash256>? Node { get; set; }
        public long Bytes { get; } = body.Length + 256 + EnvelopeBytes(envelopes);
        private static long EnvelopeBytes(Dictionary<ValueHash256, Transaction>? envelopes)
        {
            long bytes = 0;
            if (envelopes is not null)
                foreach (Transaction transaction in envelopes.Values) bytes += TxDecoder.Instance.GetLength(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping) + 128;
            return bytes;
        }
    }

    private readonly Dictionary<ValueHash256, Entry> _objects = [];
    private readonly Dictionary<ValueHash256, ValueHash256> _primary = [];
    private readonly Dictionary<ValueHash256, ValueHash256> _byEnvelope = [];
    private readonly LinkedList<ValueHash256> _order = new();

    public long Bytes { get; private set; }
    public int Count => _objects.Count;

    public bool TryGet(in ValueHash256 objectId, out Entry entry) => _objects.TryGetValue(objectId, out entry!);

    public bool Contains(in ValueHash256 objectId) => _objects.ContainsKey(objectId);

    public bool TryLookup(in LeanSelector selector, out Entry entry)
    {
        entry = null!;
        ValueHash256 objectId = selector.LookupKey;
        if (selector.Kind == LeanProtocol.KindBlockProof && !_primary.TryGetValue(PrimaryKey(selector.Kind, selector.LookupKey), out objectId))
            return false;
        return _objects.TryGetValue(objectId, out entry!) && entry.Descriptor.Kind == selector.Kind && entry.Descriptor.ProfileId == selector.ProfileId;
    }

    /// <summary>Finds an envelope retained for a hash entry of a stored wrapper.</summary>
    public bool TryGetEnvelope(in ValueHash256 transaction, out Transaction envelope)
    {
        envelope = null!;
        return _byEnvelope.TryGetValue(transaction, out ValueHash256 objectId) && _objects.TryGetValue(objectId, out Entry? entry)
            && entry.Envelopes?.TryGetValue(transaction, out envelope!) == true;
    }

    public bool TryAdd(Entry entry)
    {
        ValueHash256 objectId = entry.Descriptor.ObjectId;
        if (_objects.ContainsKey(objectId) || entry.Bytes > maxBytes) return false;
        while (_objects.Count >= maxObjects || entry.Bytes > maxBytes - Bytes) Remove(_order.First!.Value);
        entry.Node = _order.AddLast(objectId);
        _objects[objectId] = entry;
        Bytes += entry.Bytes;
        if (entry.Descriptor.Kind != LeanProtocol.KindWrapper)
            _primary[PrimaryKey(entry.Descriptor.Kind, entry.Descriptor.PrimaryKey)] = objectId;
        if (entry.Envelopes is not null)
            foreach (ValueHash256 transaction in entry.Envelopes.Keys) _byEnvelope[transaction] = objectId;
        return true;
    }

    /// <summary>Most recently added objects first.</summary>
    public IEnumerable<Entry> Recent()
    {
        for (LinkedListNode<ValueHash256>? node = _order.Last; node is not null; node = node.Previous)
            yield return _objects[node.Value];
    }

    private void Remove(ValueHash256 objectId)
    {
        if (!_objects.Remove(objectId, out Entry? entry)) return;
        _order.Remove(entry.Node!);
        Bytes -= entry.Bytes;
        if (entry.Descriptor.Kind != LeanProtocol.KindWrapper)
        {
            ValueHash256 key = PrimaryKey(entry.Descriptor.Kind, entry.Descriptor.PrimaryKey);
            if (_primary.TryGetValue(key, out ValueHash256 current) && current == objectId) _primary.Remove(key);
        }
        if (entry.Envelopes is not null)
            foreach (ValueHash256 transaction in entry.Envelopes.Keys)
                if (_byEnvelope.TryGetValue(transaction, out ValueHash256 current) && current == objectId) _byEnvelope.Remove(transaction);
    }

    // Block hashes and package hashes share one map, so the kind is folded into the key.
    private static ValueHash256 PrimaryKey(byte kind, in ValueHash256 key)
    {
        Span<byte> preimage = stackalloc byte[33];
        preimage[0] = kind;
        key.Bytes.CopyTo(preimage[1..]);
        return ValueKeccak.Compute(preimage);
    }
}

internal sealed class LeanNodeBudget(PublicKey? key)
{
    public PublicKey? Key { get; } = key;
    public List<LeanPeer> Connections { get; } = [];
    public long ChargedBytes { get; set; }
    public int Assemblies { get; set; }
    public int Stalls { get; set; }
    public long ThrottledUntil { get; set; }
    public int LiveRequests
    {
        get { int count = 0; foreach (LeanPeer peer in Connections) count += peer.Live.Count; return count; }
    }
    public int ServingRequests
    {
        get { int count = 0; foreach (LeanPeer peer in Connections) count += peer.Serving.Count; return count; }
    }
}

/// <summary>A negotiated peer's request, credit and suppression state, guarded by the transport lock.</summary>
internal sealed class LeanPeer(ILeanLink link, LeanStatusMessage status, LeanNodeBudget node)
{
    public ILeanLink Link { get; } = link;
    public LeanNodeBudget Node { get; } = node;
    public LeanStatusMessage Status { get; } = status;
    public bool Closed { get; set; }

    public ulong LastIssued { get; set; }
    public Dictionary<ulong, LeanOutgoingRequest> Live { get; } = [];

    public ulong LastIncoming { get; set; }
    public Dictionary<ulong, LeanServing> Serving { get; } = [];

    public LeanBoundedSet Known { get; } = new(LeanLimits.MaxKnownPerPeer);

    /// <summary>Wanted objects this peer announced while the assembly limits were reached, oldest first.</summary>
    public List<(LeanDescriptor Descriptor, long Announced)> Deferred { get; } = [];

    /// <summary>Control messages awaiting sending, queued under the transport lock so request IDs leave in order.</summary>
    public ConcurrentQueue<LeanMessage> Outgoing { get; } = new();

    /// <summary>1 while one thread sends <see cref="Outgoing"/>.</summary>
    public int Sending;

    public long ChargedBytes { get => Node.ChargedBytes; set => Node.ChargedBytes = value; }
    public int Assemblies { get => Node.Assemblies; set => Node.Assemblies = value; }
    public int Stalls { get => Node.Stalls; set => Node.Stalls = value; }
    public long ThrottledUntil { get => Node.ThrottledUntil; set => Node.ThrottledUntil = value; }
    public bool CanRequest(long now) => !Closed && Node.LiveRequests < LeanProtocol.MaxRequestsPerPeer && ThrottledUntil <= now;

    public override string ToString() => Link.Description;
}

internal abstract class LeanOutgoingRequest(ulong id, long now)
{
    public ulong Id { get; } = id;
    public long Created { get; } = now;
    public long LastProgress { get; set; } = now;
    public bool Progressed { get; set; }

    /// <summary>Cancel was sent: the request keeps its slot until a terminal response or expiry, and its payloads are discarded.</summary>
    public bool Cancelled { get; set; }
}

internal sealed class LeanObjectsRequest(ulong id, long now, LeanSelector[] selectors) : LeanOutgoingRequest(id, now)
{
    public LeanSelector[] Selectors { get; } = selectors;
    public TaskCompletionSource<LeanObjectResult[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class LeanChunksRequest(ulong id, long now, LeanAssembly assembly, int[] indices) : LeanOutgoingRequest(id, now)
{
    public LeanAssembly Assembly { get; } = assembly;
    public int[] Indices { get; } = indices;

    /// <summary>Position in <see cref="Indices"/> of the next acceptable Chunk; responses arrive in request order.</summary>
    public int Next { get; set; }

    /// <summary>Indices answered by a Chunk, whether retained, duplicated or discarded locally.</summary>
    public bool[] Answered { get; } = new bool[indices.Length];
}

/// <summary>A GetChunks request being served to a peer.</summary>
internal sealed class LeanServing(ulong id, ValueHash256 objectId)
{
    public ulong Id { get; } = id;
    public ValueHash256 ObjectId { get; } = objectId;
    public CancellationTokenSource Cancellation { get; } = new();
    public bool CompleteWritten { get; set; }
}

/// <summary>A receiver's partial copy of one object, shared by every peer and request that supplies it.</summary>
internal sealed class LeanAssembly(LeanDescriptor descriptor, long now, long metadataBytes)
{
    public LeanDescriptor Descriptor { get; } = descriptor;
    public bool[] Chunks { get; } = new bool[descriptor.ChunkCount];
    public LeanPeer?[] InFlight { get; } = new LeanPeer?[descriptor.ChunkCount];
    public int Received { get; set; }
    public long Created { get; } = now;
    public long Updated { get; set; } = now;
    public long MetadataBytes { get; } = metadataBytes;
    public LeanPeer? Origin { get; set; }
    public List<LeanPeer> Sources { get; } = [];

    /// <summary>Peers whose chunks went into the completed body, captured before their charges are released.</summary>
    public List<LeanPeer> Contributors { get; } = [];
    public LeanPeer? CurrentSource { get; set; }
    public int SourceAttempts { get; set; }
    public bool RetrySource { get; set; }
    public LeanPeer? CompletionSource { get; set; }
    public long RecoveryBytes { get; set; }

    /// <summary>Whether the reconstructed object goes through kind validation and, if valid, into the served store.</summary>
    public bool Validate { get; set; }

    /// <summary>Cancels validation and transaction recovery of a completed body when the assembly expires.</summary>
    public CancellationTokenSource? Processing { get; set; }

    /// <summary>Whether processing of the completed body has begun, so expiry cancels it instead of releasing the body.</summary>
    public bool Started { get; set; }

    /// <summary>Wrappers waiting on this object for direct transaction recovery; its new chunks advance their idle timers.</summary>

    /// <summary>Callers waiting for the integrity-checked body: kind-2 sidecar fetches and transaction-hash recovery.</summary>
    public List<TaskCompletionSource<LeanFetchedBody?>> Waiters { get; } = [];
    public int BodyLeases { get; set; }
    public bool ReleaseRequested { get; set; }
    public bool Dropped { get; set; }
    public bool Queued { get; set; }
    public bool MetadataReleased { get; set; }
    public byte[]? Body { get; set; }

    public bool IsComplete => Received == Chunks.Length;


    public void AddSource(LeanPeer peer)
    {
        if (!Sources.Contains(peer) && Sources.Count < LeanLimits.MaxSourcesPerAssembly) Sources.Add(peer);
    }
}

/// <summary>Owns a fetched body and its byte reservation through the caller's verification.</summary>
internal sealed class LeanFetchedBody(byte[] body, Action? release = null) : IDisposable
{
    private Action? _release = release;
    public byte[] Body { get; } = body;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
