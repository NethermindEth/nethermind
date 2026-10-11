// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>The connection side of a negotiated <c>lean/1</c> peer.</summary>
internal interface ILeanLink
{
    string Description { get; }

    /// <summary>Queues a control message.</summary>
    void Send(LeanMessage message);

    /// <summary>Writes one Chunk after backpressure clears; false when the connection can no longer carry it.</summary>
    ValueTask<bool> SendChunkAsync(ChunkMessage message, CancellationToken cancellationToken);

    /// <summary>Stops the transfer and disconnects the peer for invalid data or a protocol violation.</summary>
    void Penalize(string reason);
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
    /// <param name="transactions">Hashes of the kind-1 body's full entries, answering transaction-hash lookups.</param>
    /// <param name="envelopes">Envelopes retained for the kind-1 body's hash entries, so the transactions it offers by hash
    /// stay recoverable through GetTransactions after leaving the pool.</param>
    internal sealed class Entry(LeanDescriptor descriptor, byte[] body, LeanChunkTree tree, LeanHeaderSkeleton? skeleton, ValueHash256[] transactions,
        Dictionary<ValueHash256, byte[]>? envelopes = null)
    {
        public LeanDescriptor Descriptor { get; } = descriptor;
        public byte[] Body { get; } = body;
        public LeanChunkTree Tree { get; } = tree;
        public LeanHeaderSkeleton? Skeleton { get; } = skeleton;
        public ValueHash256[] Transactions { get; } = transactions;
        public Dictionary<ValueHash256, byte[]>? Envelopes { get; } = envelopes;
        public LinkedListNode<ValueHash256>? Node { get; set; }
        public long Bytes { get; } = body.Length + (2L << tree.Depth) * 32 + (skeleton?.Encoded.Length ?? 0) + 512 + EnvelopeBytes(envelopes);

        private static long EnvelopeBytes(Dictionary<ValueHash256, byte[]>? envelopes)
        {
            long bytes = 0;
            if (envelopes is not null)
                foreach (byte[] envelope in envelopes.Values) bytes += envelope.Length + 64;
            return bytes;
        }
    }

    private readonly Dictionary<ValueHash256, Entry> _objects = [];
    private readonly Dictionary<ValueHash256, ValueHash256> _primary = [];
    private readonly Dictionary<ValueHash256, ValueHash256> _byTransaction = [];
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
        if (selector.LookupKind == LeanProtocol.LookupTransaction)
        {
            if (!_byTransaction.TryGetValue(selector.LookupKey, out objectId)) return false;
        }
        else if (selector.Kind != LeanProtocol.KindWrapper && !_primary.TryGetValue(PrimaryKey(selector.Kind, selector.LookupKey), out objectId))
            return false;
        return _objects.TryGetValue(objectId, out entry!) && entry.Descriptor.Kind == selector.Kind && entry.Descriptor.ProfileId == selector.ProfileId;
    }

    /// <summary>Finds an envelope retained for a hash entry of a stored wrapper.</summary>
    public bool TryGetEnvelope(in ValueHash256 transaction, out byte[] envelope)
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
        foreach (ValueHash256 transaction in entry.Transactions) _byTransaction[transaction] = objectId;
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
        foreach (ValueHash256 transaction in entry.Transactions)
            if (_byTransaction.TryGetValue(transaction, out ValueHash256 current) && current == objectId) _byTransaction.Remove(transaction);
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

/// <summary>A negotiated peer's request, credit and suppression state, guarded by the transport lock.</summary>
/// <remarks>Request IDs are per connection; requests, buffered bytes and verification work are charged to the
/// <see cref="Node"/> shared by every connection of one authenticated node key.</remarks>
internal sealed class LeanPeer(ILeanLink link, LeanStatusMessage status, LeanNodeBudget? node = null)
{
    public ILeanLink Link { get; } = link;
    public LeanStatusMessage Status { get; } = status;
    public LeanNodeBudget Node { get; } = node ?? new LeanNodeBudget(null);
    public bool Closed { get; set; }

    public ulong LastIssued { get; set; }
    public Dictionary<ulong, LeanOutgoingRequest> Live { get; } = [];

    public ulong LastIncoming { get; set; }
    public Dictionary<ulong, LeanServing> Serving { get; } = [];

    public LeanBoundedSet Known { get; } = new(LeanLimits.MaxKnownPerPeer);
    public long ChargedBytes { get => Node.ChargedBytes; set => Node.ChargedBytes = value; }
    public int Assemblies { get => Node.Assemblies; set => Node.Assemblies = value; }
    public int Stalls { get => Node.Stalls; set => Node.Stalls = value; }
    public long ThrottledUntil { get => Node.ThrottledUntil; set => Node.ThrottledUntil = value; }

    public bool CanRequest(long now) => !Closed && Node.LiveRequests < LeanProtocol.MaxRequestsPerPeer && ThrottledUntil <= now;

    /// <summary>Whether the node already has <see cref="LeanProtocol.MaxRequestsPerPeer"/> incoming requests being served.</summary>
    public bool ServingFull => Node.ServingRequests >= LeanProtocol.MaxRequestsPerPeer;

    public override string ToString() => Link.Description;
}

/// <summary>Budgets shared by every <c>lean/1</c> connection of one node, whichever transport carries it.</summary>
/// <remarks>EIP-8437: separate connections, including RLPx and ethp2p, do not grant separate per-peer budgets.</remarks>
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
        get
        {
            int count = 0;
            foreach (LeanPeer peer in Connections) count += peer.Live.Count;
            return count;
        }
    }

    public int ServingRequests
    {
        get
        {
            int count = 0;
            foreach (LeanPeer peer in Connections) count += peer.Serving.Count;
            return count;
        }
    }
}

internal abstract class LeanOutgoingRequest(ulong id, long now)
{
    public ulong Id { get; } = id;
    public long Created { get; } = now;
    public long LastProgress { get; set; } = now;
    public bool Progressed { get; set; }

    /// <summary>Cancel was sent: the request keeps its slot until a terminal response or expiry, and its payloads are discarded.</summary>
    public bool Cancelled { get; set; }

    /// <summary>An ethp2p response stream was opened for this request; a second one is a protocol violation.</summary>
    public bool Streamed { get; set; }
}

internal sealed class LeanObjectsRequest(ulong id, long now, LeanSelector[] selectors) : LeanOutgoingRequest(id, now)
{
    public LeanSelector[] Selectors { get; } = selectors;
    public TaskCompletionSource<LeanObjectResult[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class LeanTransactionsRequest(ulong id, long now, ValueHash256[] hashes) : LeanOutgoingRequest(id, now)
{
    public ValueHash256[] Hashes { get; } = hashes;
    public TaskCompletionSource<(LeanResultStatus Status, byte[] Envelope)[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
internal sealed class LeanAssembly(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, long now, long metadataBytes)
{
    public LeanDescriptor Descriptor { get; } = descriptor;
    public LeanHeaderSkeleton? Skeleton { get; } = skeleton;
    public LeanBranchVerifier Verifier { get; } = new(descriptor);
    public byte[]?[] Chunks { get; } = new byte[descriptor.ChunkCount][];
    public LeanPeer?[] ChargedTo { get; } = new LeanPeer?[descriptor.ChunkCount];
    public LeanPeer?[] InFlight { get; } = new LeanPeer?[descriptor.ChunkCount];
    public int Received { get; set; }
    public long Created { get; } = now;
    public long Updated { get; set; } = now;
    public long MetadataBytes { get; } = metadataBytes;
    public LeanPeer? Origin { get; set; }
    public List<LeanPeer> Sources { get; } = [];

    /// <summary>Peers whose chunks went into the completed body, captured before their charges are released.</summary>
    public List<LeanPeer> Contributors { get; } = [];
    public int NextSource { get; set; }

    /// <summary>Whether the reconstructed object goes through kind validation and, if valid, into the served store.</summary>
    public bool Validate { get; set; }

    /// <summary>Cancels validation and transaction recovery of a completed body when the assembly expires.</summary>
    public CancellationTokenSource? Processing { get; set; }

    /// <summary>Whether processing of the completed body has begun, so expiry cancels it instead of releasing the body.</summary>
    public bool Started { get; set; }

    /// <summary>Wrappers waiting on this object for direct transaction recovery; its new chunks advance their idle timers.</summary>
    public List<LeanAssembly> Dependents { get; } = [];

    /// <summary>Callers waiting for the integrity-checked body: kind-2 sidecar fetches and transaction-hash recovery.</summary>
    public List<TaskCompletionSource<byte[]?>> Waiters { get; } = [];
    public bool Dropped { get; set; }
    public bool Queued { get; set; }
    public bool MetadataReleased { get; set; }
    public byte[]? Body { get; set; }

    /// <summary>The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the accepted broadcast session that supplied the body.</summary>
    public long? BroadcastStarted { get; set; }

    public bool IsComplete => Received == Chunks.Length;

    public long ChargeOf(int index) => Descriptor.ChunkLength(index) + Descriptor.Depth * 32L + LeanLimits.ChunkBookkeepingBytes;

    public void AddSource(LeanPeer peer)
    {
        if (!Sources.Contains(peer) && Sources.Count < LeanLimits.MaxSourcesPerAssembly) Sources.Add(peer);
    }
}
