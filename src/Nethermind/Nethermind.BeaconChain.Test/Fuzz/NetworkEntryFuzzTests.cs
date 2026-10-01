// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
using Nethermind.Serialization.Ssz;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Fuzz;

// The pubsub validator runs on the network thread and the framing reader on every inbound stream, so neither may throw anything undocumented.
[Parallelizable(ParallelScope.All)]
public class NetworkEntryFuzzTests
{
    private static readonly ulong WallSlot = FirstGloasSlot + 1;
    private static readonly ulong FuluSlot = FirstGloasSlot - 1;
    private static readonly byte[] GloasDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch);
    private static readonly byte[] FuluDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch - 1);
    private const string MalformedSnappyBlock = "Malformed snappy block";

    [Test]
    public void Request_framing_reader_refuses_hostile_streams_only_as_a_req_resp_error(
        [ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed, [Values] bool allowEmpty)
    {
        (byte[] Framed, byte[] Ssz)[] requests = [.. Payloads(seed).Select(static ssz => (FrameRequest(ssz), ssz))];
        if (allowEmpty)
        {
            requests = [.. requests, (FrameRequest([]), [])];
        }

        foreach ((byte[] framed, byte[] ssz) in requests)
        {
            Assert.That(ReadRequest(framed, allowEmpty), Is.EqualTo(ssz), "a framed request reads back to its payload");
            if (ssz.Length > 1)
            {
                // phase0 p2p: the length prefix is the uncompressed length, so frames decoding to more bytes than it are invalid.
                byte[] understated = [.. Varint((ulong)ssz.Length - 1), .. framed.AsSpan(Varint((ulong)ssz.Length).Length)];
                Assert.That(() => ReadRequest(understated, allowEmpty),
                    Throws.TypeOf<Eth2ReqRespException>().With.Message.Contains("more than the declared"), "frames longer than the length prefix are refused");
            }
        }

        byte[][] framings = [.. requests.Select(static r => r.Framed)];
        SszFuzzer.RunPrefixes(framings, 256, framed => ReadRequest(framed, allowEmpty), static e => e is Eth2ReqRespException);
        SszFuzzer.Run(seed, framings, 400, framed => ReadRequest(framed, allowEmpty), static e => e is Eth2ReqRespException);
    }

    // Byte-level mutation of whole frames seldom yields a short, well-framed data frame, so frames are built here field by field.
    [Test]
    public void Framing_readers_refuse_hostile_snappy_frames_only_as_a_req_resp_error(
        [ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed, [Values] bool response)
    {
        Random random = new(seed);
        int malformedBlocks = 0;
        SszFuzzer.Run(seed, [[0]], 400, _ =>
        {
            byte[] framed = HostileFraming(random);
            try
            {
                if (response)
                {
                    ReadResponse([ReqRespFraming.ResponseCode.Success, .. framed], contextLength: 0);
                }
                else
                {
                    ReadRequest(framed, allowEmpty: false);
                }
            }
            catch (Eth2ReqRespException e) when (e.Message.StartsWith(MalformedSnappyBlock, StringComparison.Ordinal))
            {
                malformedBlocks++;
                throw;
            }
            catch (Exception e) when (e is not Eth2ReqRespException)
            {
                throw new InvalidOperationException($"{e.GetType().Name} escaped for input {SszFuzzer.Preview(framed)}", e);
            }
        }, static e => e is Eth2ReqRespException);

        Assert.That(malformedBlocks, Is.GreaterThan(0), "no input reached the snappy block preamble check");
    }

    // A well-framed compressed frame whose snappy preamble is not a valid varint: the length read throws before any CRC check.
    [Test]
    public void Request_framing_reader_refuses_a_compressed_frame_with_a_malformed_snappy_preamble()
    {
        byte[] framed = Convert.FromHexString("0a" + "ff060000734e61507059" + "00080000" + "00000000" + "ffffffff");
        Eth2ReqRespException refusal = Assert.Throws<Eth2ReqRespException>(() => ReadRequest(framed, allowEmpty: false))!;
        Assert.That(refusal.Message, Does.StartWith(MalformedSnappyBlock));
    }

    // A data frame opens with a 4-byte CRC; a shorter one is refused where it ends, before any later frame is read.
    [Test]
    public void Request_framing_reader_refuses_a_data_frame_too_short_for_its_crc([Values(0x00, 0x01)] byte frameType, [Range(0, 3)] int dataLength)
    {
        byte[] shortFrame = [0x01, 0xff, 0x06, 0x00, 0x00, .. "sNaPpY"u8, frameType, (byte)dataLength, 0x00, 0x00, .. new byte[dataLength]];
        byte[] nextFrame = [0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2a];
        using MemoryStream stream = new([.. shortFrame, .. nextFrame]);

        Assert.That(() => ReqRespFraming.ReadRequestAsync(stream, ReqRespFraming.MaxPayloadSize, CancellationToken.None).GetAwaiter().GetResult(),
            Throws.TypeOf<Eth2ReqRespException>().With.Message.EqualTo("Snappy data frame without stream identifier or CRC"));
        Assert.That(stream.Position, Is.EqualTo(shortFrame.Length), "no byte past the short frame is read");
    }

    // An empty request has no data frame to end it, so its framing runs to the half-close: any framing that decodes to no bytes
    // (repeated stream identifiers, padding, skippable chunks) is legal, and one that is truncated, starts elsewhere or carries data is not.
    [Test]
    public void Request_framing_reader_takes_an_empty_request_only_with_framing_that_decodes_to_nothing(
        [ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed)
    {
        byte[] streamIdentifier = [0xff, 0x06, 0x00, 0x00, .. "sNaPpY"u8];
        byte[][] legal = [[], streamIdentifier, [.. streamIdentifier, .. streamIdentifier], [.. streamIdentifier, 0xfe, 0x00, 0x00, 0x00], [.. streamIdentifier, 0x80, 0x02, 0x00, 0x00, 0x2a, 0x2a]];
        Assert.That(legal.Select(static trailing => ReadRequest([0x00, .. trailing], allowEmpty: true)), Has.All.Empty);

        Random random = new(seed);
        for (int i = 0; i < 200; i++)
        {
            byte[] trailing = random.Next(3) switch
            {
                0 => [(byte)random.Next(0xff), .. RandomBytes(random, random.Next(0, 24))],
                1 => streamIdentifier[..random.Next(1, streamIdentifier.Length)],
                _ => [.. streamIdentifier, .. UncompressedFrameWithData(random)],
            };

            Assert.That(() => ReadRequest([0x00, .. trailing], allowEmpty: true), Throws.TypeOf<Eth2ReqRespException>(), SszFuzzer.Preview(trailing));
        }
    }

    [Test]
    public void Response_framing_reader_refuses_hostile_streams_only_as_a_req_resp_error(
        [ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed, [Values(0, ReqRespFraming.ForkContextLength)] int contextLength)
    {
        byte[][] payloads = Payloads(seed);
        byte[] context = contextLength == 0 ? [] : GloasDigest;
        using MemoryStream stream = new();
        foreach (byte[] payload in payloads)
        {
            ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, context, payload, CancellationToken.None).GetAwaiter().GetResult();
        }

        ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.ResourceUnavailable, "not held", CancellationToken.None).GetAwaiter().GetResult();
        byte[] framed = stream.ToArray();

        List<ResponseChunk> chunks = ReadResponse(framed, contextLength);
        Assert.That(chunks.Select(static c => c.Payload), Is.EqualTo(payloads.Append("not held"u8.ToArray())), "a framed response reads back chunk by chunk");

        SszFuzzer.Run(seed, [framed], 400, bytes => ReadResponse(bytes, contextLength), static e => e is Eth2ReqRespException);
    }

    [Test]
    public void Gossip_snappy_decompression_never_throws([ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed)
    {
        byte[][] compressed = [.. Payloads(seed).Select(static ssz => Snappy.CompressToArray(ssz))];
        SszFuzzer.Run(seed, compressed, 1000, static data => Eth2MessageId.TryDecompress(data, Eth2MessageId.MaxGossipSize, out _), static _ => false);
    }

    [Test]
    public void Gossip_validator_returns_a_verdict_for_any_message([ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed)
    {
        (GossipMessageValidator validator, GossipRouter router, ColumnGossipRouter columns) = CreateValidator();
        GossipInput[] topics = GossipInputs(seed);
        Random random = new(seed);
        int decodable = 0;
        int undecodable = 0;

        long InvalidSszDrops() => router.GetDropCount(GossipDropReason.InvalidSsz) + columns.GetDropCount(ColumnGossipDropReason.InvalidSsz);

        // One fuzz input picks a topic and a payload; the payload is mutated before or after compression.
        byte[][] selectors = [[0]];
        SszFuzzer.Run(seed, selectors, 600, _ =>
        {
            GossipInput input = topics[random.Next(topics.Length)];
            string topic = input.Topic;
            byte[] data = random.Next(2) == 0
                ? Snappy.CompressToArray(SszFuzzer.Mutate(random, input.Ssz))
                : SszFuzzer.Mutate(random, [.. input.Ssz.Select(static s => Snappy.CompressToArray(s))]);
            bool topicMutated = random.Next(20) == 0;
            if (topicMutated)
            {
                topic = MutateTopic(random, topic);
            }

            bool signed = random.Next(20) == 0;
            Message message = new()
            {
                Topic = topic,
                Data = ByteString.CopyFrom(data),
            };
            if (signed)
            {
                message.Signature = ByteString.CopyFrom([1]);
            }

            long invalidSszBefore = InvalidSszDrops();
            long unknownTopicBefore = router.GetDropCount(GossipDropReason.UnknownTopic);
            MessageValidity validity = validator.Verify(message);
            string context = $"{topic}, data {SszFuzzer.Preview(data)}";
            if (validity == MessageValidity.Accepted && !IsGloasColumnTopic(topic))
            {
                throw new AssertionException($"Only a Gloas data column sidecar may be Accepted, but {context} was");
            }

            // StrictNoSign: a signed message is rejected whatever it carries.
            if (signed)
            {
                AssertVerdict(validity, MessageValidity.Rejected, "a signed message", context);
                return;
            }

            if (topicMutated)
            {
                return;
            }

            if (input.TopicVerdict is { } topicVerdict)
            {
                AssertVerdict(validity, topicVerdict, "a topic refused before its payload is read", context);
                if (router.GetDropCount(GossipDropReason.UnknownTopic) != unknownTopicBefore + 1)
                {
                    throw new AssertionException($"A refused topic was not counted as unknown: {context}");
                }

                return;
            }

            if (input.Decode is null)
            {
                return;
            }

            // phase0 p2p: a payload that is not valid snappy or not the topic's SSZ type is rejected; one that is must never count as malformed.
            if (Eth2MessageId.TryDecompress(data, Eth2MessageId.MaxGossipSize, out byte[]? payload) != SnappyDecodeResult.Decoded || !Decodes(input.Decode, payload!))
            {
                undecodable++;
                AssertVerdict(validity, MessageValidity.Rejected, "an undecodable payload", context);
                return;
            }

            decodable++;
            if (InvalidSszDrops() != invalidSszBefore)
            {
                throw new AssertionException($"A payload the topic's type decodes was dropped as malformed SSZ, so the validator threw on it: {context}");
            }
        }, static _ => false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decodable, Is.GreaterThan(0), "no decodable payload reached the validator");
            Assert.That(undecodable, Is.GreaterThan(0), "no undecodable payload reached the validator");
        }
    }

    // Verify runs on the pubsub thread, so a store fault while validating must end as a verdict rather than escape.
    [Test]
    public void Gossip_validator_rejects_an_envelope_whose_block_read_faults()
    {
        Hash256 root = Keccak.Compute("held block");
        FaultingReadsDb blocks = new();
        blocks[root.Bytes] = [1];
        IColumnsDb<BeaconChainDbColumns> db = Substitute.For<IColumnsDb<BeaconChainDbColumns>>();
        db.GetColumnDb(Arg.Any<BeaconChainDbColumns>()).Returns(static _ => new MemDb());
        db.GetColumnDb(BeaconChainDbColumns.Blocks).Returns(blocks);
        (GossipMessageValidator validator, _, _) = CreateValidator(new BeaconChainStore(db, Sepolia));
        byte[] envelope = SszFuzzer.ValidEncodings<SignedExecutionPayloadEnvelope>(SszFuzzer.Seeds[0], 1, e =>
        {
            e.Message!.BeaconBlockRoot = root;
            e.Message.Payload!.SlotNumber = WallSlot;
            e.Message.ExecutionRequests = new ExecutionRequestsGloas();
        })[0];
        Message message = new() { Topic = Topic(GloasDigest, GossipTopics.ExecutionPayload), Data = ByteString.CopyFrom(Snappy.CompressToArray(envelope)) };

        Assert.That(validator.Verify(message), Is.EqualTo(MessageValidity.Rejected));
        Assert.That(blocks.FaultedReads, Is.GreaterThan(0), "the validator read the faulting block");
    }

    // A 5-byte message declaring GOSSIP_MAX_SIZE passes the phase0 p2p size cap, so only a bound on snappy expansion keeps it from buying a 10 MiB buffer.
    [Test]
    public void Gossip_snappy_bomb_is_refused_without_allocating_its_declared_length()
    {
        (GossipMessageValidator validator, _, _) = CreateValidator();
        string topic = Topic(GloasDigest, GossipTopics.BeaconBlock);
        byte[] bomb = [0x80, 0x80, 0x80, 0x05, 0x00];
        Message message = new() { Topic = topic, Data = ByteString.CopyFrom(bomb) };
        validator.Verify(new Message { Topic = topic, Data = ByteString.CopyFrom([0x01, 0x00]) });
        Eth2MessageId.Compute(topic, [0x01, 0x00]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        MessageValidity validity = validator.Verify(message);
        long verifyAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        Eth2MessageId.Compute(topic, bomb);
        long idAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(verifyAllocated, Is.LessThan(1 << 20), "bytes allocated by Verify");
            Assert.That(idAllocated, Is.LessThan(1 << 20), "bytes allocated by the message id");
        }
    }

    private sealed class FaultingReadsDb : MemDb
    {
        public int FaultedReads { get; private set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            FaultedReads++;
            throw new IOException("the block column is unreadable");
        }
    }

    private static bool Decodes(Action<byte[]> decode, byte[] payload)
    {
        try
        {
            decode(payload);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void AssertVerdict(MessageValidity actual, MessageValidity expected, string what, string context)
    {
        if (actual != expected)
        {
            throw new AssertionException($"Expected {expected} for {what}, got {actual}: {context}");
        }
    }

    private static bool IsGloasColumnTopic(string topic) =>
        GossipTopics.TryParse(topic, out byte[]? digest, out string? name)
        && digest.AsSpan().SequenceEqual(GloasDigest)
        && GossipTopics.TryParseDataColumnSidecarTopicName(name, out _);

    private static string MutateTopic(Random random, string topic)
    {
        char[] chars = topic.ToCharArray();
        chars[random.Next(chars.Length)] = (char)random.Next(0x20, 0x7f);
        return new string(chars);
    }

    private static byte[][] Payloads(int seed) =>
    [
        .. SszFuzzer.ValidEncodings<StatusMessageV2>(seed, 2),
        .. SszFuzzer.ValidEncodings<BeaconBlocksByRootRequest>(seed, 2),
        .. SszFuzzer.ValidEncodings<SignedBeaconBlockGloas>(seed, 2),
        // A payload over one 64 KiB snappy block spans several frames.
        .. SszFuzzer.ValidEncodings<DataColumnSidecarGloas>(seed, 1, static s => s.Column = [.. Enumerable.Repeat(s.Column!.FirstOrDefault(), 40)]),
    ];

    /// <param name="Decode">Decodes a payload as the type the router decodes this topic as; <c>null</c> for a topic the validator does not handle.</param>
    /// <param name="TopicVerdict">The verdict the topic alone decides, whatever the payload; <c>null</c> when the payload decides.</param>
    private sealed record GossipInput(string Topic, byte[][] Ssz, Action<byte[]>? Decode, MessageValidity? TopicVerdict = null);

    private static GossipInput[] GossipInputs(int seed)
    {
        List<GossipInput> inputs =
        [
            new(Topic(GloasDigest, GossipTopics.BeaconBlock), SszFuzzer.ValidEncodings<SignedBeaconBlockGloas>(seed, 3, static b => { b.Message!.Slot = WallSlot; b.Message.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockRoot = b.Message.ParentRoot; }),
                DecodeAs<SignedBeaconBlockGloas>()),
            new(Topic(FuluDigest, GossipTopics.BeaconBlock), SszFuzzer.ValidEncodings<SignedBeaconBlock>(seed, 3, static b => b.Message!.Slot = FuluSlot),
                DecodeAs<SignedBeaconBlock>()),
            new(Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), SszFuzzer.ValidEncodings<SignedAggregateAndProofGloas>(seed, 3, static a => AdjustVote(a.Message!.Aggregate!.Data!, WallSlot)),
                DecodeAs<SignedAggregateAndProofGloas>()),
            new(Topic(FuluDigest, GossipTopics.BeaconAggregateAndProof), SszFuzzer.ValidEncodings<SignedAggregateAndProof>(seed, 3, static a => AdjustVote(a.Message!.Aggregate!.Data!, FuluSlot)),
                DecodeAs<SignedAggregateAndProof>()),
            new(Topic(GloasDigest, GossipTopics.AttesterSlashing), SszFuzzer.ValidEncodings<AttesterSlashingGloas>(seed, 3, static s => { AdjustVote(s.Attestation1!.Data!, WallSlot); AdjustVote(s.Attestation2!.Data!, WallSlot); }),
                DecodeAs<AttesterSlashingGloas>()),
            new(Topic(FuluDigest, GossipTopics.AttesterSlashing), SszFuzzer.ValidEncodings<AttesterSlashing>(seed, 3, static s => { AdjustVote(s.Attestation1!.Data!, FuluSlot); AdjustVote(s.Attestation2!.Data!, FuluSlot); }),
                DecodeAs<AttesterSlashing>()),
            new(Topic(GloasDigest, GossipTopics.ExecutionPayload), SszFuzzer.ValidEncodings<SignedExecutionPayloadEnvelope>(seed, 3, static e => e.Message!.Payload!.SlotNumber = WallSlot),
                DecodeAs<SignedExecutionPayloadEnvelope>()),
            new(Topic(GloasDigest, GossipTopics.VoluntaryExit), SszFuzzer.ValidEncodings<SignedVoluntaryExit>(seed, 2), null),
            // A digest in effect at no epoch near the wall clock, or a Gloas-only topic under the Fulu digest, is ignored rather than penalised.
            new(Topic([1, 2, 3, 4], GossipTopics.BeaconBlock), SszFuzzer.ValidEncodings<SignedBeaconBlockGloas>(seed, 2), null, MessageValidity.Ignored),
            new(Topic(FuluDigest, GossipTopics.ExecutionPayload), SszFuzzer.ValidEncodings<SignedExecutionPayloadEnvelope>(seed, 2), null, MessageValidity.Ignored),
        ];

        foreach (ulong subnet in (ulong[])[0, 5, 127, 128])
        {
            // Fulu p2p: a subnet id at or past DATA_COLUMN_SIDECAR_SUBNET_COUNT is an unknown topic, which phase0 p2p rejects.
            MessageValidity? topicVerdict = subnet >= Eip7594DasConstants.DataColumnSidecarSubnetCount ? MessageValidity.Rejected : null;
            inputs.Add(new(Topic(GloasDigest, GossipTopics.DataColumnSidecarTopicName(subnet)),
                SszFuzzer.ValidEncodings<DataColumnSidecarGloas>(seed, 2, s => { s.Index = subnet; s.Slot = WallSlot; }),
                DecodeAs<DataColumnSidecarGloas>(), topicVerdict));
            inputs.Add(new(Topic(FuluDigest, GossipTopics.DataColumnSidecarTopicName(subnet)),
                SszFuzzer.ValidEncodings<DataColumnSidecar>(seed, 2, s => { s.Index = subnet; s.SignedBlockHeader!.Message!.Slot = FuluSlot; }),
                DecodeAs<DataColumnSidecar>(), topicVerdict));
        }

        return [.. inputs];
    }

    private static Action<byte[]> DecodeAs<T>() where T : ISszCodec<T> => static payload => T.Decode(payload, out T _);

    private static void AdjustVote(AttestationData data, ulong slot)
    {
        data.Slot = slot;
        data.Index = 0;
        data.Target!.Epoch = Sepolia.GetEpoch(slot);
    }

    private static (GossipMessageValidator Validator, GossipRouter Router, ColumnGossipRouter Columns) CreateValidator(BeaconChainStore? store = null)
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + WallSlot * Sepolia.SecondsPerSlot + 6));
        SlotClock clock = new(Sepolia, timestamper);
        BeaconChainStatusHolder status = new(Sepolia, timestamper)
        {
            CurrentStatus = new StatusMessageV2 { ForkDigest = GloasDigest, FinalizedRoot = Hash256.Zero, FinalizedEpoch = Sepolia.GloasForkEpoch - 2, HeadRoot = Hash256.Zero },
        };
        ColumnGossipRouter columns = new(Sepolia, clock, LimboLogs.Instance, status: status);
        columns.Start(static _ => Substitute.For<ITopic>(), GloasDigest, [.. Enumerable.Range(0, (int)Eip7594DasConstants.DataColumnSidecarSubnetCount).Select(static i => (ulong)i)]);
        columns.SubscribeDigest(FuluDigest);
        GossipRouter router = new(Sepolia, clock, LimboLogs.Instance, store, status);
        return (new GossipMessageValidator(router, columns, Sepolia, clock), router, columns);
    }

    private static string Topic(byte[] digest, string name) => GossipTopics.Topic(digest, name);

    private static byte[] HostileFraming(Random random)
    {
        using MemoryStream stream = new();
        stream.Write(Varint(random.Next(8) == 0 ? (ulong)random.Next(0, 1 << 20) : (ulong)random.Next(1, 64)));
        if (random.Next(8) != 0)
        {
            stream.Write([0xff, 0x06, 0x00, 0x00, .. "sNaPpY"u8]);
        }

        for (int frames = 1 + random.Next(4); frames > 0; frames--)
        {
            byte type = random.Next(10) switch
            {
                < 4 => 0x00,
                4 => 0x01,
                5 => (byte)random.Next(0x02, 0x80),
                6 => (byte)random.Next(0x80, 0xfe),
                7 => 0xfe,
                _ => 0xff,
            };
            byte[] body = type switch
            {
                0x00 => [.. RandomBytes(random, random.Next(8) == 0 ? random.Next(4) : 4), .. SnappyBlockPreamble(random)],
                0x01 => RandomBytes(random, random.Next(48)),
                0xff => random.Next(2) == 0 ? [.. "sNaPpY"u8] : RandomBytes(random, random.Next(10)),
                _ => RandomBytes(random, random.Next(16)),
            };
            int length = random.Next(16) == 0 ? random.Next(0, 1 << 24) : body.Length;
            stream.Write([type, (byte)length, (byte)(length >> 8), (byte)(length >> 16)]);
            stream.Write(body);
        }

        byte[] framed = stream.ToArray();
        return random.Next(8) == 0 ? framed[..random.Next(framed.Length + 1)] : framed;
    }

    // The snappy block after a compressed frame's CRC: empty, an unterminated or oversized length varint, or a real block.
    private static byte[] SnappyBlockPreamble(Random random) => random.Next(4) switch
    {
        0 => [],
        1 => [.. Enumerable.Repeat((byte)0xff, 1 + random.Next(6))],
        2 => [.. Varint((ulong)random.NextInt64()), .. RandomBytes(random, random.Next(8))],
        _ => Snappy.CompressToArray(RandomBytes(random, random.Next(1, 64))),
    };

    /// <summary>An uncompressed snappy frame carrying 1 to 19 bytes under a random checksum.</summary>
    private static byte[] UncompressedFrameWithData(Random random)
    {
        byte[] data = RandomBytes(random, random.Next(1, 20));
        return [0x01, (byte)(4 + data.Length), 0x00, 0x00, .. RandomBytes(random, 4), .. data];
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static byte[] Varint(ulong value)
    {
        List<byte> bytes = [];
        for (; value >= 0x80; value >>= 7)
        {
            bytes.Add((byte)(value | 0x80));
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }

    private static byte[] FrameRequest(byte[] ssz)
    {
        using MemoryStream stream = new();
        ReqRespFraming.WriteRequestAsync(stream, ssz, CancellationToken.None).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static byte[] ReadRequest(byte[] framed, bool allowEmpty)
    {
        using MemoryStream stream = new(framed);
        return ReqRespFraming.ReadRequestAsync(stream, ReqRespFraming.MaxPayloadSize, CancellationToken.None, allowEmpty).GetAwaiter().GetResult();
    }

    private static List<ResponseChunk> ReadResponse(byte[] framed, int contextLength)
    {
        using MemoryStream stream = new(framed);
        List<ResponseChunk> chunks = [];
        while (ReqRespFraming.ReadResponseChunkAsync(stream, contextLength, ReqRespFraming.MaxPayloadSize, CancellationToken.None).GetAwaiter().GetResult() is { } chunk)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }
}
