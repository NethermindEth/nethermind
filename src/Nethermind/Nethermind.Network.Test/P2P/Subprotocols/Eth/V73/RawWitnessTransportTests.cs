// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using NSubstitute;
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

    // EIP-8437: in mode 0 too, a dependency/witness count mismatch is malformed; and mode 2's caps of 64 transactions, counting
    // those sharing a dependency, and 16 raw entries are hard item-count violations, with the boundary accepted.
    [TestCase(MempoolWrapper.ModeDirect, 1, 0, false)]
    [TestCase(MempoolWrapper.ModeDirect, 1, 2, false)]
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.MaxRawTransactions, 1, true)]
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.MaxRawTransactions + 1, 1, false)]
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.MaxRawEntries, MempoolWrapper.MaxRawEntries, true)]
    [TestCase(MempoolWrapper.ModeRaw, MempoolWrapper.MaxRawEntries + 1, MempoolWrapper.MaxRawEntries + 1, false)]
    public void Witness_counts_and_mode_2_caps_are_encoding_rules(byte mode, int transactions, int entries, bool parses)
    {
        // One leanSTARK dependency per raw entry, shared round-robin by the transactions; mode 0 carries a single leanSPHINCS one.
        FrameDependency[] deps = mode == MempoolWrapper.ModeRaw
            ? [.. Enumerable.Range(0, entries).Select(static i => new FrameDependency(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(BitConverter.GetBytes(i)), default))]
            : [new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("sig"), default)];
        Transaction[] txs = [.. Enumerable.Range(0, transactions).Select(i => Declaring(i, deps[i % deps.Length]))];
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        Rlp[] witnesses = mode == MempoolWrapper.ModeRaw
            ? [.. canonical.Select(static _ => Rlp.Encode(Rlp.Encode(new byte[] { 1 }), Rlp.Encode(new byte[32]), Rlp.Encode(new byte[8])))]
            : [.. Enumerable.Range(0, entries).Select(static _ => Rlp.Encode(new byte[] { 1 }))];
        byte[] body = Rlp.Encode(
            Rlp.Encode([.. txs.OrderBy(static t => t.Hash!.ToString()).Select(static t => Rlp.Encode(Envelope(t)))]),
            Rlp.Encode(mode),
            Rlp.Encode(Rlp.Encode([.. canonical.Select(static d => { byte[] b = new byte[Eip8288Constants.DependencyTripleLength]; d.WriteTo(b); return Rlp.Encode(b); })]),
                Rlp.Encode(witnesses))).Bytes;

        if (parses) Assert.That(WrapperBodies.ParseWrapper(body), Has.Length.EqualTo(transactions));
        else Assert.That(() => WrapperBodies.ParseWrapper(body), Throws.InstanceOf<RlpException>());
    }

    // EIP-8437: a body breaking mode 2's transaction count, raw-entry count or per-entry byte bound is malformed although its
    // announced length is valid, and is rejected before any signature or proof is verified.
    [TestCase("transaction count")]
    [TestCase("raw-entry count")]
    [TestCase("raw-entry bytes")]
    public async Task Over_limit_mode_2_bodies_are_malformed_before_any_verification(string scenario)
    {
        (int transactions, int entries, int signatureBytes) = scenario switch
        {
            "transaction count" => (MempoolWrapper.MaxRawTransactions + 1, 1, 8),
            "raw-entry count" => (MempoolWrapper.MaxRawEntries + 1, MempoolWrapper.MaxRawEntries + 1, 8),
            _ => (1, 1, MempoolWrapper.MaxRawWitnessBytes),
        };
        byte[] body = RawBody(transactions, entries, signatureBytes);
        Assert.That(body.Length, Is.LessThanOrEqualTo(Eth73Protocol.MaxWrapperBytes), "the announced length is valid");
        using WrapperTestNode node = new(rawWitnesses: true);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);

        node.Transport.OnAnnounce(peer, new([Announcement(MempoolWrapper.ModeRaw, body.Length, ValueKeccak.Compute(body))]));
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>().Single().RequestId, [Entry(body)]));
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Not.Empty, "malformed, not merely invalid");
            Assert.That(node.Verifier.Calls, Is.Zero);
            node.Recognized!.DidNotReceiveWithAnyArgs().Verify(default, default!, out _);
            Assert.That(node.Transport.IsStored(ValueKeccak.Compute(body)), Is.False);
        }
    }

    // EIP-8437: mode-2 traffic leaves proof-backed fetches request slots and in-flight bytes of their own.
    [Test]
    public void Mode_2_requests_leave_proof_backed_fetches_room()
    {
        using WrapperTestNode node = new(rawWitnesses: true);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        int rawAnnouncements = (WrapperTransportLimits.MaxRawRequestsPerPeer + 1) * Eth73Protocol.MaxWrappersPerRequest;
        WrapperAnnouncement[] raw = [.. Enumerable.Range(0, rawAnnouncements)
            .Select(static i => Announcement(MempoolWrapper.ModeRaw, 1024, ValueKeccak.Compute(BitConverter.GetBytes(i)),
                new WrapperCoverage(ValueKeccak.Compute(BitConverter.GetBytes(-i)), ValueKeccak.Compute(BitConverter.GetBytes(i))))).OrderBy(static a => a.Hash)];
        node.Transport.OnAnnounce(peer, new([.. raw.Take(Eth73Protocol.MaxAnnouncements)]));
        node.Transport.OnAnnounce(peer, new([.. raw.Skip(Eth73Protocol.MaxAnnouncements)]));
        byte[] proven = Wrapper(64, FrameTransaction(1));

        node.Transport.OnAnnounce(peer, new([Describe(proven)]));

        GetProofWrappersMessage[] requests = link.Sent<GetProofWrappersMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests.Count(r => r.Hashes.Any(h => raw.Any(a => a.Hash == h))), Is.EqualTo(WrapperTransportLimits.MaxRawRequestsPerPeer));
            Assert.That(requests.Any(r => r.Hashes.Contains(Describe(proven).Hash)), Is.True, "the proof-backed wrapper still gets a request slot");
            Assert.That(requests.All(r => r.Hashes.All(h => raw.Any(a => a.Hash == h)) || r.Hashes.All(h => raw.All(a => a.Hash != h))), Is.True,
                "a request never mixes modes");
            Assert.That(node.Transport.RawChargedBytes, Is.InRange(1, WrapperTransportLimits.MaxRawInFlightBytes));
        }
    }

    /// <summary>A mode-2 body of <paramref name="transactions"/> transactions sharing <paramref name="entries"/> leanSTARK dependencies
    /// round-robin, each with a raw entry carrying a signature of <paramref name="signatureBytes"/>.</summary>
    private static byte[] RawBody(int transactions, int entries, int signatureBytes)
    {
        FrameDependency[] deps = [.. Enumerable.Range(0, entries).Select(static i => new FrameDependency(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute(BitConverter.GetBytes(i)), default))];
        Transaction[] txs = [.. Enumerable.Range(0, transactions).Select(i => Declaring(i, deps[i % deps.Length]))];
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        return Rlp.Encode(
            Rlp.Encode([.. txs.OrderBy(static t => t.Hash!.ToString()).Select(static t => Rlp.Encode(Envelope(t)))]),
            Rlp.Encode(MempoolWrapper.ModeRaw),
            Rlp.Encode(Rlp.Encode([.. canonical.Select(static d => { byte[] b = new byte[Eip8288Constants.DependencyTripleLength]; d.WriteTo(b); return Rlp.Encode(b); })]),
                Rlp.Encode([.. canonical.Select(_ => Rlp.Encode(Rlp.Encode(new byte[] { 1 }), Rlp.Encode(new byte[32]), Rlp.Encode(new byte[signatureBytes])))]))).Bytes;
    }

    /// <summary>A frame transaction, distinct by <paramref name="seed"/>, declaring <paramref name="dependency"/>.</summary>
    private static Transaction Declaring(int seed, FrameDependency dependency)
    {
        Transaction transaction = FrameTransaction(seed, dependency.Scheme);
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, dependency.VerificationGas, Int256.UInt256.Zero,
            Eip8288Dependencies.Serialize([dependency])), new(FrameMode.Sender, FrameFlags.None, Address.Zero, 21_000, Int256.UInt256.Zero, BitConverter.GetBytes(seed))];
        transaction.Hash = new Hash256(ValueKeccak.Compute(Envelope(transaction)));
        return transaction;
    }

    // EIP-8437: a node recognizing no programs ignores a mode 2 announcement without fetching it, but still runs the wire checks.
    [Test]
    public void Mode_2_announcements_keep_their_wire_checks_where_unsupported()
    {
        byte[] badLength = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(2), Eth73Rlp.EncodeUInt((ulong)Eth73Protocol.MaxWrapperBytes + 1),
            Eth73Rlp.EncodeBytes(new byte[32]), Eth73Rlp.EncodeBytes(new byte[32]), Eth73Rlp.EncodeBytes(new byte[32]));
        Assert.That(() => WrapperAnnouncement.Decode(badLength), Throws.InstanceOf<RlpException>());
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
    [TestCase("missing entry")]
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
            "missing entry" => [Raw()],
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
