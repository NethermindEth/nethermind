// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 mode 2 on the wire: raw signatures for recognized leaf programs.</summary>
public class RawWitnessTransportTests
{
    private static Transaction GenericTransaction(int seed) => FrameTransaction(seed, Eip8288Constants.LeanStarkScheme);

    // EIP-8437: clients MUST ignore announcements of unsupported modes without penalties after checking the message's encoding.
    // A node recognizing no program does not support mode 2.
    [TestCase(2UL)]
    [TestCase(3UL)]
    [TestCase(256UL)]
    [TestCase(ulong.MaxValue)]
    public void Unsupported_modes_are_ignored_without_penalty(ulong mode)
    {
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        byte[] entry = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(mode), Eth73Rlp.EncodeUInt(100), Eth73Rlp.EncodeBytes(ValueKeccak.Compute("w").Bytes),
            Eth73Rlp.EncodeBytes(new byte[32]), Eth73Rlp.EncodeBytes(new byte[32]));
        NewProofWrapperHashesMessageSerializer serializer = new();
        NewProofWrapperHashesMessage message = serializer.Deserialize(Eth73Rlp.EncodeList(entry));

        node.Transport.OnAnnounce(peer, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Wrappers.AcceptsRawWitnesses, Is.False);
            Assert.That(node.Transport.FetchCount, Is.Zero);
            Assert.That(link.Sent<GetProofWrappersMessage>(), Is.Empty);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    [Test]
    public void Mode_2_is_fetched_by_a_node_that_recognizes_a_program()
    {
        using WrapperTestNode node = new(rawWitnesses: true);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        byte[] wrapper = RawWrapper(64, GenericTransaction(1));

        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Describe(wrapper).Mode, Is.EqualTo(MempoolWrapper.ModeRaw));
            Assert.That(link.Sent<GetProofWrappersMessage>().Single().Hashes, Is.EqualTo(new[] { Describe(wrapper).Hash }));
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    // EIP-8437 Test Cases: mode 2 and mode 1 with identical coverage hints. Raw-only coverage cannot suppress mode 0 or 1; proven
    // coverage may suppress mode 2, and mode 2 another mode 2.
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.ModeRecursive, true)]
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.ModeDirect, true)]
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.ModeRaw, false)]
    [TestCase(MempoolWrapper.ModeRecursive, MempoolWrapper.ModeRaw, false)]
    [TestCase(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRaw, false)]
    public void Held_coverage_suppresses_fetching_only_where_mode_2_rules_allow(byte held, byte announced, bool fetched)
    {
        using WrapperTestNode node = new(rawWitnesses: true);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        Transaction transaction = GenericTransaction(1);
        byte[] Of(byte mode, int seed) => mode switch
        {
            MempoolWrapper.ModeRaw => RawWrapper(64 + seed, transaction),
            MempoolWrapper.ModeDirect => DirectWrapper(transaction),
            _ => Wrapper(64, seed, transaction),
        };
        byte[] heldWrapper = Of(held, 1), announcedWrapper = Of(announced, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.PublishWrapper(heldWrapper), Is.True);
            Assert.That(Describe(announcedWrapper).Coverage, Is.EqualTo(Describe(heldWrapper).Coverage), "same transactions and dependencies");
        }

        node.Transport.OnAnnounce(peer, new([Describe(announcedWrapper)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Sent<GetProofWrappersMessage>(), fetched ? Has.Length.EqualTo(1) : Is.Empty);
            Assert.That(WrapperStore.Suppresses(held, announced), Is.EqualTo(!fetched));
        }
    }

    // EIP-8437: a held mode-2 wrapper the recipient already has does not make announcing a mode-0 or 1 wrapper of the same
    // transactions redundant; a proof-backed one the recipient has makes a mode-2 wrapper redundant.
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.ModeDirect, true)]
    [TestCase(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRaw, false)]
    public void Exchanged_raw_coverage_never_suppresses_announcing_proofs(byte exchanged, byte published, bool announced)
    {
        using WrapperTestNode node = new(rawWitnesses: true);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        Transaction transaction = GenericTransaction(1);
        byte[] Of(byte mode) => mode == MempoolWrapper.ModeRaw ? RawWrapper(64, transaction) : DirectWrapper(transaction);
        byte[] first = Of(exchanged), second = Of(published);
        node.Transport.OnAnnounce(peer, new([Describe(first)]));
        Assert.That(node.Transport.PublishWrapper(first), Is.True);
        link.Clear();

        Assert.That(node.Transport.PublishWrapper(second), Is.True);

        WrapperAnnouncement[] sent = [.. link.Sent<NewProofWrapperHashesMessage>().SelectMany(static m => m.Announcements)];
        Assert.That(sent.Any(a => a.Hash == Describe(second).Hash), Is.EqualTo(announced));
    }

    // A raw candidate's transactions are never pending until proven, so a new peer must not make its held mode-2 wrapper look
    // stale: it stays offered and is announced to the peer, while a wrapper of transactions neither pending nor held is dropped.
    [TestCase(true)]
    [TestCase(false)]
    public void Held_raw_wrapper_survives_and_is_announced_to_a_new_peer(bool held)
    {
        using WrapperTestNode node = new(rawWitnesses: true);
        Transaction transaction = GenericTransaction(1);
        byte[] wrapper = RawWrapper(64, transaction);
        Assert.That(node.Transport.PublishWrapper(wrapper), Is.True);
        if (held)
        {
            RlpReader reader = new(wrapper);
            node.Raw!.TryAdd(wrapper, ValueKeccak.Compute(wrapper), MempoolWrapperDecoder.Instance.Decode(ref reader)!, Eip8288Verifier.Pinned, null, local: true);
        }
        FakeWrapperLink link = new();

        node.Connect(link);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsStored(Describe(wrapper).Hash), Is.EqualTo(held));
            Assert.That(link.Sent<NewProofWrapperHashesMessage>().SelectMany(static m => m.Announcements).Any(a => a.Hash == Describe(wrapper).Hash),
                Is.EqualTo(held));
        }
    }

    // EIP-8437: raw candidates have byte and count budgets of their own that preserve capacity for proof-backed traffic.
    [Test]
    public void Raw_wrappers_evict_only_raw_wrappers_from_the_offer_store()
    {
        WrapperStore store = new(maxBytes: 1 << 20, maxObjects: 4, maxRawBytes: 1 << 20, maxRawObjects: 2);
        static WrapperStore.Entry Entry(byte[] wrapper)
        {
            ValueHash256[] hashes = WrapperBodies.ParseWrapper(wrapper, out WrapperShape shape, out WrapperCoverage coverage);
            return new(WrapperAnnouncement.Create(wrapper, shape.Mode, coverage), wrapper, hashes, Eip8288Verifier.Pinned, shape,
                WrapperCompaction.EncodingHashes(wrapper));
        }
        byte[] proven = DirectWrapper(GenericTransaction(0));
        byte[][] raws = [.. Enumerable.Range(1, 3).Select(static i => RawWrapper(64, GenericTransaction(i)))];

        Assert.That(store.TryAdd(Entry(proven)), Is.True);
        foreach (byte[] raw in raws) Assert.That(store.TryAdd(Entry(raw)), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Contains(ValueKeccak.Compute(proven)), Is.True);
            Assert.That(store.Contains(ValueKeccak.Compute(raws[0])), Is.False, "the oldest raw wrapper gave way");
            Assert.That(store.Count, Is.EqualTo(3));
            Assert.That(store.RawBytes, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Mode_2_shape_counts_raw_entry_bytes()
    {
        byte[] wrapper = RawWrapper(500, GenericTransaction(1), FrameTransaction(2));

        WrapperBodies.ParseWrapper(wrapper, out WrapperShape shape, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shape.Mode, Is.EqualTo(MempoolWrapper.ModeRaw));
            Assert.That(shape.ProofBytes, Is.GreaterThan(500));
            Assert.That(shape.VerificationWork, Is.EqualTo((long)(Eip8288Constants.LeanStarkVerificationGas + Eip8288Constants.LeanSphincsVerificationGas)));
            Assert.That(WrapperBodies.ReadMode(wrapper), Is.EqualTo(MempoolWrapper.ModeRaw));
        }
    }

    // EIP-8437: a raw entry for a non-generic dependency, an entry count other than the dependencies' or no raw entry is
    // malformed.
    [TestCase("raw entry for leanSPHINCS")]
    [TestCase("no raw entry")]
    [TestCase("extra entry")]
    public void Malformed_mode_2_bodies_are_rejected(string scenario)
    {
        Transaction generic = GenericTransaction(1), sphincs = FrameTransaction(2);
        byte[] body = RawWrapper(64, generic, sphincs);
        RlpReader reader = new(body);
        MempoolWrapper decoded = MempoolWrapperDecoder.Instance.Decode(ref reader)!;
        static Rlp Raw() => Rlp.Encode(Rlp.Encode(new byte[] { 1 }), Rlp.Encode(new byte[32]), Rlp.Encode(new byte[64]));
        Rlp[] entries = scenario switch
        {
            "raw entry for leanSPHINCS" => [.. decoded.Deps.Select(static _ => Raw())],
            "no raw entry" => [.. decoded.Deps.Select(static _ => Rlp.Encode(new byte[] { 1 }))],
            _ => [.. decoded.Deps.Select(d => d.Scheme == Eip8288Constants.LeanStarkScheme ? Raw() : Rlp.Encode(new byte[] { 1 })), Rlp.Encode(new byte[] { 1 })],
        };
        byte[] malformed = Rlp.Encode(
            Rlp.Encode([.. decoded.Transactions.Select(static t => Rlp.Encode(Envelope(t)))]),
            Rlp.Encode(MempoolWrapper.ModeRaw),
            Rlp.Encode(Rlp.Encode([.. decoded.Deps.Select(static d => { byte[] b = new byte[Eip8288Constants.DependencyTripleLength]; d.WriteTo(b); return Rlp.Encode(b); })]),
                Rlp.Encode(entries))).Bytes;

        Assert.That(() => WrapperBodies.ParseWrapper(malformed), Throws.InstanceOf<RlpException>());
    }
}
