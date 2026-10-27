// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>The client integration cases EIP-8437 lists under Test Cases, with valid dependency-bearing transactions A and B
/// under a fixed target verifier unless stated otherwise.</summary>
public class Eip8437ClientCaseTests
{
    private static void Answer(WrapperTestNode node, WrapperPeer peer, FakeWrapperLink link, params byte[][] wrappers) =>
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>()[^1].RequestId,
            [.. wrappers.Select(Entry)]));

    private static ValueHash256[] AnnouncedTo(FakeWrapperLink target) =>
        [.. target.Sent<NewProofWrapperHashesMessage>().SelectMany(static m => m.Announcements).Select(static d => d.Hash)];

    // Suppress wrappers covering already-covered transactions unless cheaper. A peer announces mode-1 W1 covering A and W2
    // covering B. W3, mode 1 over both, is announced because it combines two proofs into one. After W3 is sent to the peer,
    // W4 with the same transactions, mode and size is not announced.
    [Test]
    public void Redundant_coverage_is_announced_only_when_it_reduces_verification_work()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] w1 = Wrapper(64, a), w2 = Wrapper(64, b), w3 = Wrapper(64, 3, a, b), w4 = Wrapper(64, 4, a, b);
        Assert.That(Describe(w4).Hash, Is.Not.EqualTo(Describe(w3).Hash));
        using WrapperTestNode node = new();
        FakeWrapperLink link = new(), fresh = new("fresh");
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([.. new[] { Describe(w1), Describe(w2) }.OrderBy(d => d.Hash)]));
        Assert.That(node.Transport.PublishWrapper(w1), Is.True);
        Assert.That(node.Transport.PublishWrapper(w2), Is.True);
        Assert.That(node.Transport.PublishWrapper(w3), Is.True);
        Assert.That(AnnouncedTo(link), Is.EqualTo(new[] { Describe(w3).Hash }), "W1 and W2 came from the peer; W3 halves its verifications");

        node.Transport.OnGetProofWrappers(peer, new GetProofWrappersMessage(1, false, [Describe(w3).Hash]));
        Assert.That(link.Sent<ProofWrappersMessage>().Single().Entries.Select(static e => e.Wrapper), Is.EqualTo(new[] { w3 }));
        node.Tick(TimeSpan.FromSeconds(1));
        Assert.That(node.Transport.PublishWrapper(w4), Is.True);
        node.Tick(TimeSpan.FromSeconds(1));
        node.Connect(fresh);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AnnouncedTo(link), Is.EqualTo(new[] { Describe(w3).Hash }), "W4 adds nothing the peer's W3 lacks");
            Assert.That(AnnouncedTo(fresh), Does.Contain(Describe(w4).Hash), "a peer without that coverage still learns of W4");
        }
    }

    // Suppression exception: a mode-1 proof is announced where the peer's existing coverage is not one reusable mode-1 proof,
    // even if the direct witnesses it holds are cheaper to verify.
    [Test]
    public void Mode1_proof_is_announced_over_cheaper_direct_coverage()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] direct = DirectWrapper(a, b), aggregate = Wrapper(64, a, b), another = Wrapper(64, 5, a, b);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(direct)]));
        Assert.That(node.Transport.PublishWrapper(direct), Is.True);
        Assert.That(node.Transport.PublishWrapper(aggregate), Is.True);
        node.Transport.OnGetProofWrappers(peer, new(1, true, [Describe(aggregate).Hash]));
        node.Tick(TimeSpan.FromSeconds(1));
        Assert.That(node.Transport.PublishWrapper(another), Is.True);
        Assert.That(AnnouncedTo(link), Is.EqualTo(new[] { Describe(aggregate).Hash }),
            "the first aggregate is IL-reusable where the direct witnesses are not; the second adds nothing over it");
    }

    // Before local activation, ignore wrapper messages without penalties and send none (see also Eth73ProtocolHandlerTests).
    [Test]
    public void Inactive_transport_sends_nothing()
    {
        using WrapperTestNode node = new();
        node.Tree.Head.Returns((Block?)null);
        FakeWrapperLink link = new();
        node.Transport.Accept(link);
        node.Tick(WrapperTransportLimits.Tick);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.PublishWrapper(Wrapper(64, FrameTransaction(1))), Is.False);
            Assert.That(link.Sent<NewProofWrapperHashesMessage>(), Is.Empty);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    // Discard a wrapper failing proof or dependency validation without penalizing its sender; malformed transport or
    // mismatched announced hashes remain penalizable (see ProofWrapperTransportTests for the verifier change).
    [TestCase("proof", false)]
    [TestCase("dependency union", false)]
    [TestCase("announced hash", true)]
    public async Task Proof_and_dependency_failures_are_discarded_without_penalty(string scenario, bool penalized)
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = scenario == "dependency union" ? MismatchedUnion(a, b) : Wrapper(64, a, b);
        using WrapperTestNode node = new();
        node.Verifier.Valid = scenario != "proof";
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        if (scenario == "announced hash")
            node.Transport.OnProofWrappers(peer, new(link.Sent<GetProofWrappersMessage>()[^1].RequestId, [new(Describe(wrapper).Hash, Wrapper(64, 9, a, b))]));
        else
            Answer(node, peer, link, wrapper);
        // A penalized source leaves the fetch waiting for another announcer.
        if (!penalized) await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Has.Length.EqualTo(penalized ? 1 : 0));
            Assert.That(node.Pending, Is.Empty);
            Assert.That(node.Transport.IsStored(Describe(wrapper).Hash), Is.False);
        }
    }

    /// <summary>A mode-1 wrapper over A and B claiming only A's dependency.</summary>
    private static byte[] MismatchedUnion(Transaction a, Transaction b)
    {
        Transaction[] sorted = [.. new[] { a, b }.OrderBy(static t => t.Hash!.ToString())];
        System.Collections.Generic.List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(a, FrameTxSigHash.ComputeValue);
        return Nethermind.Consensus.ProofAggregation.MempoolWrapperDecoder.Instance.Encode(new Nethermind.Consensus.ProofAggregation.MempoolWrapper
        {
            Transactions = sorted,
            Deps = deps,
            Mode = Nethermind.Consensus.ProofAggregation.MempoolWrapper.ModeRecursive,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
        }).Bytes;
    }

    // This client never omits encodings: it serves a compact = 1 request in full and requests with compact = 0.
    [Test]
    public void Requests_are_full_and_compact_requests_are_served_in_full()
    {
        Transaction a = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, a);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        Assert.That(node.Transport.PublishWrapper(wrapper), Is.True);
        node.Transport.OnGetProofWrappers(peer, new(1, true, [Describe(wrapper).Hash]));
        node.Transport.OnGetProofWrappers(peer, new(2, true, [Describe(wrapper).Hash]));
        using WrapperTestNode requester = new();
        FakeWrapperLink source = new();
        requester.Transport.OnAnnounce(requester.Connect(source), new([Describe(wrapper)]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Sent<ProofWrappersMessage>().Select(static m => m.Entries.Single().Wrapper), Is.EqualTo(new[] { wrapper, wrapper }),
                "an encoding sent before is sent in full again");
            Assert.That(source.Sent<GetProofWrappersMessage>().Single().Compact, Is.False);
        }
    }

    // A complete wrapper can be validated without envelope requests: the transport has no other request than GetProofWrappers.
    [Test]
    public async Task Complete_wrapper_is_validated_without_envelope_requests()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, a, b);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.IsStored(Describe(wrapper).Hash) && node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(static t => t.Hash), Is.EquivalentTo(new[] { a.Hash, b.Hash }));
            Assert.That(link.Sent<Nethermind.Network.P2P.Messages.P2PMessage>().Select(static m => m.GetType()).Distinct(),
                Is.EqualTo(new[] { typeof(GetProofWrappersMessage) }));
        }
    }
}
