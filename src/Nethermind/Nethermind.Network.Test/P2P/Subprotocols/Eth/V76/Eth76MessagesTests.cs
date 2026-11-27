// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V75;
using Nethermind.Network.P2P.Subprotocols.Eth.V75.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V76;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V76;

public class Eth76MessagesTests
{
    private static readonly string VectorDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "P2P", "Subprotocols", "Eth", "V76", "Vectors");

    private static JsonElement LoadVectors(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorDirectory, $"eip8518-{name}-vectors.json"))).RootElement;

    private static IEnumerable<TestCaseData> AnnouncementVectors()
    {
        foreach (JsonElement vector in LoadVectors("announcement").EnumerateArray())
            yield return new TestCaseData(vector).SetArgDisplayNames($"mode {vector.GetProperty("mode").GetInt32()}, {vector.GetProperty("size").GetInt32()} bytes");
    }

    private static IEnumerable<TestCaseData> WrapperVectors()
    {
        foreach (JsonProperty vector in LoadVectors("wrapper").EnumerateObject())
            if (vector.Value.ValueKind == JsonValueKind.Object) yield return new TestCaseData(vector.Value).SetArgDisplayNames(vector.Name);
    }

    private static byte[] Hex(JsonElement vector, string name) => Bytes.FromHexString(vector.GetProperty(name).GetString()!);

    private static WrapperCoverage Coverage(JsonElement vector) => new(new ValueHash256(Hex(vector, "deps_hash")), new ValueHash256(Hex(vector, "txs_hash")));

    // ethereum/EIPs#12713, assets/eip-8518/announcement-vectors.json: wrapper[i] = i % 251 with fixed hints.
    [TestCaseSource(nameof(AnnouncementVectors))]
    public void Announcement_matches_eth76_announcement_vectors(JsonElement vector)
    {
        int mode = vector.GetProperty("mode").GetInt32();
        int size = vector.GetProperty("size").GetInt32();
        WrapperAnnouncement announcement = WrapperAnnouncement.Create([.. Enumerable.Range(0, size).Select(static i => (byte)(i % 251))], mode, Coverage(vector));
        WrapperAnnouncement? decoded = WrapperAnnouncement.Decode76(Hex(vector, "announcement_rlp"), out ValueHash256 hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Hash, Is.EqualTo(new ValueHash256(Hex(vector, "content_hash"))));
            Assert.That(announcement.Encoded76, Is.EqualTo(Hex(vector, "announcement_rlp")));
            Assert.That(hash, Is.EqualTo(announcement.Hash));
            Assert.That((decoded!.Mode, decoded.Length, decoded.Coverage), Is.EqualTo((mode, size, (WrapperCoverage?)Coverage(vector))));
        }
    }

    // ethereum/EIPs#12713, assets/eip-8518/wrapper-vectors.json: mode-0, mode-1 and blob-carrying mode-1 wrappers
    // with their hints, metadata, announcement, full and compact responses, requests and manifests. The synthetic frames carry no
    // gas limits, so the checker's fixed 100,000 stands in.
    [TestCaseSource(nameof(WrapperVectors))]
    public void Wrapper_vectors_match_hints_metadata_messages_and_manifests(JsonElement vector)
    {
        byte[] body = Hex(vector, "body");
        ValueHash256 hash = new(Hex(vector, "content_hash"));
        WrapperManifest manifest = WrapperManifest.Of(WrapperBodies.ParseWrapper(body, out WrapperShape shape, out WrapperCoverage coverage));
        WrapperMetadata metadata = WrapperMetadata.Of(body, static (ref Eth75RlpReader frames) => 100_000);
        WrapperMetadata? inline = shape.Mode == MempoolWrapper.ModeDirect ? metadata : null;
        Dictionary<ValueHash256, byte[]> cache = WrapperCompaction.Encodings(body).ToDictionary(static e => e.Hash, static e => e.Encoding);
        byte[] compact = new ProofWrappersMessageSerializer().Decode(Hex(vector, "compact_response_rlp")).Entries.Single().Wrapper;
        ProofWrapperManifestsMessage manifests = new ProofWrapperManifestsMessageSerializer().Decode(Hex(vector, "manifest_response_rlp"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValueKeccak.Compute(body), Is.EqualTo(hash));
            Assert.That(coverage, Is.EqualTo(Coverage(vector)), "hints hash full triples and consensus transaction hashes");
            Assert.That(metadata.Encoded, Is.EqualTo(Hex(vector, "transaction_metadata_rlp")));
            Assert.That(metadata.TransactionsDigest, Is.EqualTo(coverage.TxsHash));
            Assert.That(WrapperAnnouncement.Create(body, shape.Mode, coverage, inline).Encoded76, Is.EqualTo(Hex(vector, "announcement_rlp")));
            Assert.That(WrapperAnnouncement.Decode76(Hex(vector, "announcement_rlp"), out _)!.Metadata, Is.EqualTo(inline));
            Assert.That(new ProofWrappersMessageSerializer().Encode(new(7, [new(hash, body)])), Is.EqualTo(Hex(vector, "response_rlp")));
            Assert.That(WrapperCompaction.Compact(body, cache.ContainsKey), Is.EqualTo(compact));
            Assert.That(WrapperCompaction.Reconstruct(compact, body.Length, h => cache.GetValueOrDefault(h), out byte[]? rebuilt, out _),
                Is.EqualTo(WrapperCompaction.Outcome.Complete));
            Assert.That(rebuilt, Is.EqualTo(body));
            Assert.That(WrapperCompaction.EncodingHashes(body).Select(static h => h.ToString(false)),
                Is.EqualTo(vector.GetProperty("encoding_hashes").EnumerateArray().Select(static h => h.GetString())));
            Assert.That(new GetProofWrappersMessage76Serializer().Encode(new(7, true, [hash])), Is.EqualTo(Hex(vector, "compact_request_rlp")));
            Assert.That(new GetProofWrappersMessage76Serializer().Encode(new(8, false, [hash])), Is.EqualTo(Hex(vector, "full_request_rlp")));
            Assert.That(new GetProofWrapperManifestsMessageSerializer().Encode(new(9, [hash])), Is.EqualTo(Hex(vector, "manifest_request_rlp")));
            Assert.That(new ProofWrapperManifestsMessageSerializer().Encode(new(9, [new(hash, manifest)])), Is.EqualTo(Hex(vector, "manifest_response_rlp")));
            Assert.That(manifests.Manifests.Single(), Is.EqualTo(new ProofWrapperManifest(hash, manifest)));
            Assert.That(manifest.TransactionsDigest, Is.EqualTo(coverage.TxsHash));
        }
    }

    private static byte[] Entry(ulong mode, byte[] txsHash, byte[] metadata) => Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(mode), Eth75Rlp.EncodeUInt(1000),
        Eth75Rlp.EncodeBytes(new byte[32]), Eth75Rlp.EncodeBytes(new byte[32]), Eth75Rlp.EncodeBytes(txsHash), metadata);

    private static byte[] Metadata(byte[] hash) => Eth75Rlp.EncodeList(Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(10)),
        Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(hash)), Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[20])),
        Eth75Rlp.EncodeList(Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1))), Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1)));

    // check_vectors.py: inline metadata on mode 1, metadata whose hashes do not hash to txs-hash and the eth/75 entry shape are
    // malformed; modes other than 0 and 1 are ignored.
    [TestCase("valid", false)]
    [TestCase("mode 1 metadata", true)]
    [TestCase("txs-hash mismatch", true)]
    [TestCase("eth/75 shape", true)]
    [TestCase("mode 2", false)]
    public void Eth76_announcement_entries_are_checked(string scenario, bool malformed)
    {
        byte[] transaction = Keccak.Compute("transaction").BytesToArray();
        byte[] txsHash = ValueKeccak.Compute(transaction).ToByteArray();
        byte[] entry = scenario switch
        {
            "mode 1 metadata" => Entry(1, txsHash, Metadata(transaction)),
            "txs-hash mismatch" => Entry(0, new byte[32], Metadata(transaction)),
            "eth/75 shape" => Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(0), Eth75Rlp.EncodeUInt(1000), Eth75Rlp.EncodeBytes(new byte[32]), Eth75Rlp.EncodeList()),
            "mode 2" => Entry(2, txsHash, Eth75Rlp.EncodeList()),
            _ => Entry(0, txsHash, Metadata(transaction))
        };
        if (malformed) Assert.That(() => WrapperAnnouncement.Decode76(entry, out _), Throws.InstanceOf<RlpException>());
        else Assert.That(WrapperAnnouncement.Decode76(entry, out _) is null, Is.EqualTo(scenario == "mode 2"));
    }

    private static byte[] WidestAnnouncements()
    {
        byte[][] hashes = [.. Enumerable.Range(0, 17).Select(static i => ValueKeccak.Compute([(byte)i])).Order().Select(static h => Eth75Rlp.EncodeBytes(h.Bytes))];
        static byte[] Repeat(byte[] item) => Eth75Rlp.EncodeList([.. Enumerable.Repeat(item, 17)]);
        byte[] metadata = Eth75Rlp.EncodeList(Repeat(Eth75Rlp.EncodeUInt((Eth75Protocol.MaxWrapperBytes - 1) / 17)), Eth75Rlp.EncodeList(hashes),
            Repeat(Eth75Rlp.EncodeBytes(Enumerable.Repeat((byte)0xff, 20).ToArray())),
            Repeat(Eth75Rlp.EncodeList([.. Enumerable.Repeat(Eth75Rlp.EncodeBytes(Enumerable.Repeat((byte)0xff, 32).ToArray()), 3)])),
            Repeat(Eth75Rlp.EncodeUInt(ulong.MaxValue)));
        return Eth75Rlp.EncodeList([.. Enumerable.Range(1, Eth75Protocol.MaxAnnouncements).Select(i => Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(0),
            Eth75Rlp.EncodeUInt(Eth75Protocol.MaxWrapperBytes), Eth75Rlp.EncodeBytes([.. new byte[31], (byte)i]), Eth75Rlp.EncodeBytes(new byte[32]),
            Eth75Rlp.EncodeBytes(new byte[32]), metadata))]);
    }

    // check_vectors.py: an announcement of exactly 256 KiB, metadata of an unsupported mode included, is accepted; one byte more is
    // rejected before decoding.
    [Test]
    public void Announcement_payload_is_bounded_at_256_KiB()
    {
        static byte[] Payload(int padding) => Eth75Rlp.EncodeList(Entry(2, new byte[32], Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[padding]))));
        int padding = Eth76Protocol.MaxNewProofWrapperHashesBytes;
        while (Payload(padding).Length > Eth76Protocol.MaxNewProofWrapperHashesBytes) padding--;
        NewProofWrapperHashesMessage76Serializer serializer = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Payload(padding), Has.Length.EqualTo(1 << 18));
            Assert.That(serializer.Decode(Payload(padding)).Announcements, Is.Empty);
            Assert.That(Payload(padding + 1), Has.Length.EqualTo((1 << 18) + 1));
            Assert.That(() => serializer.Decode(Payload(padding + 1)), Throws.InstanceOf<RlpException>());
        }
    }

    // check_vectors.py: a manifest is a nonempty, strictly ascending list of 32-byte hashes, with no 17-transaction limit.
    [TestCase("valid", false)]
    [TestCase("4,097 transactions", false)]
    [TestCase("empty", true)]
    [TestCase("unordered", true)]
    [TestCase("repeated", true)]
    [TestCase("short hash", true)]
    [TestCase("nested list", true)]
    public void Manifests_are_checked_on_arrival(string scenario, bool malformed)
    {
        byte[][] hashes = [.. Enumerable.Range(1, scenario == "4,097 transactions" ? 4097 : 2)
            .Select(static i => Eth75Rlp.EncodeBytes([.. new byte[30], (byte)(i >> 8), (byte)i]))];
        byte[] encoded = scenario switch
        {
            "empty" => Eth75Rlp.EncodeList(),
            "unordered" => Eth75Rlp.EncodeList(hashes[1], hashes[0]),
            "repeated" => Eth75Rlp.EncodeList(hashes[0], hashes[0]),
            "short hash" => Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[31])),
            "nested list" => Eth75Rlp.EncodeList(Eth75Rlp.EncodeList(hashes[0])),
            _ => Eth75Rlp.EncodeList(hashes)
        };
        if (malformed) Assert.That(() => WrapperManifest.Decode(encoded), Throws.InstanceOf<RlpException>());
        else Assert.That(WrapperManifest.Decode(encoded).Encoded, Is.EqualTo(encoded));
    }

    // check_vectors.py: 64 announcements without metadata encode in 6,851 bytes and with the widest 17-transaction metadata, at the
    // largest wire integers and three fees each, in 190,660; any announcement is bounded at 256 KiB, and 32 requested hashes
    // encode in 1,072 with the compact flag and 1,071 for manifests.
    [Test]
    public void Messages_at_their_entry_limits_match_eth76_lengths()
    {
        static byte[] Word(int value)
        {
            byte[] word = new byte[32];
            word[^1] = (byte)value;
            return word;
        }
        WrapperAnnouncement[] announced = [.. Enumerable.Range(1, Eth75Protocol.MaxAnnouncements)
            .Select(static i => WrapperAnnouncement.Decode76(Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeUInt(Eth75Protocol.MaxWrapperBytes),
                Eth75Rlp.EncodeBytes(Word(i)), Eth75Rlp.EncodeBytes(new byte[32]), Eth75Rlp.EncodeBytes(new byte[32]), Eth75Rlp.EncodeList()), out _)!)];
        ValueHash256[] requested = [.. Enumerable.Range(1, Eth75Protocol.MaxWrappersPerRequest).Select(static i => new ValueHash256(Word(i)))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new NewProofWrapperHashesMessage76Serializer().Encode(new(announced)), Has.Length.EqualTo(6851));
            Assert.That(WidestAnnouncements(), Has.Length.EqualTo(190_660).And.Length.LessThan(Eth76Protocol.MaxNewProofWrapperHashesBytes));
            Assert.That(new GetProofWrappersMessage76Serializer().Encode(new(-1, true, requested)), Has.Length.EqualTo(Eth76Protocol.MaxGetProofWrappersBytes));
            Assert.That(new GetProofWrapperManifestsMessageSerializer().Encode(new(-1, requested)), Has.Length.EqualTo(Eth76Protocol.MaxGetProofWrapperManifestsBytes));
            Assert.That(Eth76Protocol.MaxNewProofWrapperHashesBytes, Is.EqualTo(1 << 18));
            Assert.That(new[] { Eth76MessageCode.GetProofWrapperManifests, Eth76MessageCode.ProofWrapperManifests, Eth76Protocol.MessageIdSpaceSize },
                Is.EqualTo(new[] { 0x1b, 0x1c, 0x1d }));
            Assert.That(Eth76Protocol.ExceedsMessageLimit(Eth75MessageCode.ProofWrappers, Eth75Protocol.LocalMaxResponseBytes + 1), Is.True);
            Assert.That(Eth76Protocol.ExceedsMessageLimit(Eth76MessageCode.ProofWrapperManifests, Eth76Protocol.LocalMaxManifestsBytes + 1), Is.True);
            Assert.That(() => new GetProofWrappersMessage76Serializer().Decode(Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(Word(1))))),
                Throws.InstanceOf<RlpException>(), "an eth/75 request lacks the compact flag");
        }
    }
}
