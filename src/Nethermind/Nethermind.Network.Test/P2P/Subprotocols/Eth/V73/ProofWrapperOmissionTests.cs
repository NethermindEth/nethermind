// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 hash-based omission between a requester and a responder.</summary>
public class ProofWrapperOmissionTests
{
    /// <summary>A requester and a holder connected to each other.</summary>
    private sealed class Pair : System.IDisposable
    {
        public WrapperTestNode Node { get; } = new();
        public WrapperTestNode Holder { get; } = new();

        /// <summary>The holder as the requester sees it.</summary>
        public FakeWrapperLink ToHolder { get; } = new("holder");

        /// <summary>The requester as the holder sees it.</summary>
        public FakeWrapperLink ToNode { get; } = new("requester");
        public WrapperPeer HolderPeer { get; }
        public WrapperPeer NodePeer { get; }

        public Pair()
        {
            HolderPeer = Node.Connect(ToHolder);
            NodePeer = Holder.Connect(ToNode);
        }

        /// <summary>Publishes <paramref name="wrapper"/> at the holder and carries its announcement, the request and the response
        /// across, returning the request and the response as sent.</summary>
        public (GetProofWrappersMessage Request, ProofWrappersMessage Response) Exchange(byte[] wrapper)
        {
            Holder.Tick(System.TimeSpan.FromSeconds(1));
            Assert.That(Holder.Transport.PublishWrapper(wrapper), Is.True);
            Node.Transport.OnAnnounce(HolderPeer, ToNode.Sent<NewProofWrapperHashesMessage>()[^1]);
            GetProofWrappersMessage request = ToHolder.Sent<GetProofWrappersMessage>()[^1];
            Holder.Transport.OnGetProofWrappers(NodePeer, request);
            ProofWrappersMessage response = ToNode.Sent<ProofWrappersMessage>()[^1];
            Node.Transport.OnProofWrappers(HolderPeer, response);
            return (request, response);
        }

        public Task Stored(byte[] wrapper) =>
            WrapperTestNode.Until(() => Node.Transport.IsStored(Describe(wrapper).Hash) && Node.Transport.FetchCount == 0);

        public void Dispose()
        {
            Node.Dispose();
            Holder.Dispose();
        }
    }

    // Compact round trip: an encoding the holder sent in full is referenced on a compact request and the requester rebuilds
    // the identical canonical wrapper from its cache.
    [Test]
    public async Task Compact_response_is_reconstructed_from_encodings_received_in_full()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] first = Wrapper(64, a), second = Wrapper(64, a, b);
        using Pair pair = new();
        (GetProofWrappersMessage firstRequest, _) = pair.Exchange(first);
        await pair.Stored(first);
        (GetProofWrappersMessage request, ProofWrappersMessage response) = pair.Exchange(second);
        await pair.Stored(second);
        byte[] sent = response.Entries.Single().Wrapper;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRequest.Compact, Is.False, "nothing was cached from the holder yet");
            Assert.That(request.Compact, Is.True, "the requester holds encodings the holder sent in full");
            Assert.That(WrapperCompaction.HasReferences(sent), Is.True);
            Assert.That(sent.Length, Is.LessThan(second.Length));
            Assert.That(response.Entries.Single().Hash, Is.EqualTo(Describe(second).Hash), "identity is the complete wrapper's");
            Assert.That(pair.Node.Pending.Select(static t => t.Hash), Is.EquivalentTo(new[] { a.Hash, b.Hash }));
            Assert.That(pair.ToHolder.Penalties, Is.Empty);
            Assert.That(pair.Node.Transport.ChargedBytes, Is.Zero);
        }
    }

    // Client case: omit an encoding previously sent in full on the connection, never one not delivered, and never on a
    // compact = 0 request.
    [Test]
    public void Responder_omits_only_encodings_it_sent_in_full_on_the_connection()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2), c = FrameTransaction(3);
        byte[] first = Wrapper(64, a, b), second = Wrapper(64, a, c);
        using WrapperTestNode node = new();
        Assert.That(node.Transport.PublishWrapper(first), Is.True);
        Assert.That(node.Transport.PublishWrapper(second), Is.True);
        FakeWrapperLink link = new(), other = new("other");
        WrapperPeer peer = node.Connect(link), otherPeer = node.Connect(other);
        node.Transport.OnGetProofWrappers(peer, new(1, true, [Describe(first).Hash]));
        node.Transport.OnGetProofWrappers(peer, new(2, false, [Describe(second).Hash]));
        node.Transport.OnGetProofWrappers(peer, new(3, true, [Describe(second).Hash]));
        node.Transport.OnGetProofWrappers(otherPeer, new(1, true, [Describe(second).Hash]));
        byte[][] sent = [.. link.Sent<ProofWrappersMessage>().Select(static m => m.Entries.Single().Wrapper)];
        byte[] compact = sent[2];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent[0], Is.EqualTo(first), "nothing was delivered on the connection yet");
            Assert.That(sent[1], Is.EqualTo(second), "compact = 0 requires complete encodings");
            Assert.That(compact.Length, Is.LessThan(second.Length));
            Assert.That(CountReferences(compact), Is.EqualTo(2), "a and c were both sent in full by now");
            Assert.That(other.Sent<ProofWrappersMessage>().Single().Entries.Single().Wrapper, Is.EqualTo(second),
                "deliveries on one connection do not permit references on another");
        }
    }

    // A missing encoding: the compact item is discarded and fetched again with compact = 0 under a fresh request ID, without
    // penalty.
    [Test]
    public async Task Cache_miss_is_retried_with_a_complete_request_without_penalty()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2), c = FrameTransaction(3);
        byte[] first = Wrapper(64, a), second = Wrapper(64, b, c);
        using Pair pair = new();
        pair.Exchange(first);
        await pair.Stored(first);
        pair.Node.Transport.OnAnnounce(pair.HolderPeer, new([Describe(second)]));
        GetProofWrappersMessage compactRequest = pair.ToHolder.Sent<GetProofWrappersMessage>()[^1];
        Assert.That(compactRequest.Compact, Is.True);
        // References to b and c, which this connection never carried in full.
        byte[] unresolvable = WrapperCompaction.Compact(second, static _ => true);
        pair.Node.Transport.OnProofWrappers(pair.HolderPeer, new(compactRequest.RequestId, [new(Describe(second).Hash, unresolvable)]));
        GetProofWrappersMessage retry = pair.ToHolder.Sent<GetProofWrappersMessage>()[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pair.ToHolder.Penalties, Is.Empty);
            Assert.That(retry.RequestId, Is.Not.EqualTo(compactRequest.RequestId));
            Assert.That(retry.Compact, Is.False);
            Assert.That(retry.Hashes, Is.EqualTo(new[] { Describe(second).Hash }));
        }
        pair.Node.Transport.OnProofWrappers(pair.HolderPeer, new(retry.RequestId, [Entry(second)]));
        await pair.Stored(second);
        Assert.That(pair.ToHolder.Penalties, Is.Empty);
    }

    // References in a compact = 0 response are rejected and penalized.
    [Test]
    public async Task Reference_in_a_complete_response_is_a_violation()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] first = Wrapper(64, a), second = Wrapper(64, a, b);
        using Pair pair = new();
        pair.Exchange(first);
        await pair.Stored(first);
        pair.Node.Transport.OnAnnounce(pair.HolderPeer, new([Describe(second)]));
        GetProofWrappersMessage compactRequest = pair.ToHolder.Sent<GetProofWrappersMessage>()[^1];
        // A cache miss forces the next request to be complete; the peer answers it with references anyway.
        pair.Node.Transport.OnProofWrappers(pair.HolderPeer, new(compactRequest.RequestId,
            [new(Describe(second).Hash, WrapperCompaction.Compact(second, static _ => true))]));
        GetProofWrappersMessage complete = pair.ToHolder.Sent<GetProofWrappersMessage>()[^1];
        Assert.That(complete.Compact, Is.False);
        byte[] resolvable = WrapperCompaction.Compact(second, hash => hash == ValueKeccak.Compute(Envelope(a)));
        pair.Node.Transport.OnProofWrappers(pair.HolderPeer, new(complete.RequestId, [new(Describe(second).Hash, resolvable)]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pair.ToHolder.Penalties.Single(), Does.Contain("compact = 0"));
            Assert.That(pair.Node.Transport.IsStored(Describe(second).Hash), Is.False);
            Assert.That(pair.Node.Transport.ChargedBytes, Is.Zero);
        }
    }

    // The compact form fits the announced length, but the reconstructed wrapper exceeds it.
    [Test]
    public async Task Reconstruction_beyond_the_announced_length_is_rejected()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] first = Wrapper(64, a), second = Wrapper(64, a, b);
        using Pair pair = new();
        pair.Exchange(first);
        await pair.Stored(first);
        byte[] compact = WrapperCompaction.Compact(second, hash => hash == ValueKeccak.Compute(Envelope(a)));
        WrapperAnnouncement understated = WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(1),
            Eth73Rlp.EncodeUInt((ulong)compact.Length), Eth73Rlp.EncodeBytes(Describe(second).Hash.Bytes)));
        pair.Node.Transport.OnAnnounce(pair.HolderPeer, new([understated]));
        GetProofWrappersMessage request = pair.ToHolder.Sent<GetProofWrappersMessage>()[^1];
        Assert.That(request.Compact, Is.True);
        pair.Node.Transport.OnProofWrappers(pair.HolderPeer, new(request.RequestId, [new(Describe(second).Hash, compact)]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pair.ToHolder.Penalties.Single(), Does.Contain("announced length"));
            Assert.That(pair.Node.Pending.Select(static t => t.Hash), Is.EqualTo(new[] { a.Hash }));
        }
    }

    [Test]
    public async Task Encoding_cache_is_released_with_the_connection()
    {
        byte[] wrapper = Wrapper(64, FrameTransaction(1));
        using Pair pair = new();
        pair.Exchange(wrapper);
        await pair.Stored(wrapper);
        Assert.That(pair.Node.Transport.CachedEncodingBytes, Is.Positive);
        pair.Node.Transport.Remove(pair.HolderPeer);
        Assert.That(pair.Node.Transport.CachedEncodingBytes, Is.Zero);
    }

    [Test]
    public void Encoding_cache_is_bounded_per_peer_and_globally()
    {
        WrapperEncodingBudget budget = new(3000);
        WrapperEncodingCache first = new(budget, 2000), second = new(budget, 2000);
        byte[] Encoding(int seed) => [.. Enumerable.Repeat((byte)seed, 904)];
        ValueHash256 Key(int seed) => ValueKeccak.Compute(Encoding(seed));
        for (int i = 0; i < 3; i++) first.Add(Key(i), Encoding(i));
        for (int i = 3; i < 6; i++) second.Add(Key(i), Encoding(i));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Get(Key(0)), Is.Null, "the oldest entry gives way within the per-peer bound");
            Assert.That(first.Get(Key(2)), Is.Not.Null);
            Assert.That(budget.Used, Is.LessThanOrEqualTo(3000));
            Assert.That(second.Get(Key(5)), Is.Not.Null, "a peer under global pressure evicts its own entries");
        }
        first.Close();
        second.Close();
        Assert.That(budget.Used, Is.Zero);
    }

    private static int CountReferences(byte[] wrapper)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader transactions = outer.ReadList().ReadList();
        int references = 0;
        while (transactions.HasMore)
        {
            if (transactions.IsListNext) references++;
            transactions.ReadItem();
        }
        return references;
    }
}
