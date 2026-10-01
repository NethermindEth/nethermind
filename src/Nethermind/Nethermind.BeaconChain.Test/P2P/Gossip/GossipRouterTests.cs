// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Google.Protobuf;
using Multiformats.Address;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public partial class GossipRouterTests
{
    // A Fulu/BPO2-era mainnet slot so messages exercise the current digest configuration.
    private const ulong CurrentSlot = 13_410_304;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    public void Retiring_digest_sends_unsubscribe_and_skips_validation_without_caching([Values] bool column, [Values] bool getTopic)
    {
        using PubsubRouter pubsub = new(new PeerStore(), new PubsubSettings
        {
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
            GetMessageId = static message => new MessageId(Eth2MessageId.Compute(message.Topic, message.Data.Span)),
        });
        int verified = 0;
        GossipTopicSubscriptions subscriptions = new(pubsub, _ => { verified++; return MessageValidity.Ignored; });
        pubsub.VerifyMessage = subscriptions.Verify;
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot));
        GossipRouter gossip = CreateRouter();
        ColumnGossipRouter columns = new(Spec, new SlotClock(Spec, new ManualTimestamper(
            DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot))), LimboLogs.Instance);
        if (column)
        {
            columns.Start(subscriptions.GetTopic, digest, [0]);
        }
        else
        {
            gossip.Start(subscriptions.GetTopic, digest);
        }

        string topicId = GossipTopics.Topic(digest, column ? GossipTopics.DataColumnSidecarTopicName(0) : GossipTopics.BeaconBlock);
        ITopic topic = subscriptions.GetTopic(topicId);
        (PeerId peer, List<Rpc> sent, Action<Rpc> receive) = ConnectSubscribedPeer(pubsub, topicId);
        ((IRoutingStateContainer)pubsub).Mesh[topicId].Add(peer);
        sent.Clear();

        if (column)
        {
            columns.UnsubscribeDigest(digest);
        }
        else
        {
            gossip.UnsubscribeDigest(digest);
        }

        Rpc publish = new();
        publish.Publish.Add(new Message { Topic = topicId, Data = ByteString.CopyFrom([1]) });
        receive(publish);
        receive(publish);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topic.IsSubscribed, Is.False);
            Assert.That(sent.SelectMany(static rpc => rpc.Subscriptions).Any(s => s.Topicid == topicId && !s.Subscribe), Is.True);
            Assert.That(sent.SelectMany(static rpc => rpc.Control?.Prune ?? []).Any(p => p.TopicID == topicId), Is.True);
            Assert.That(verified, Is.Zero);
        }

        if (getTopic)
        {
            topic = subscriptions.GetTopic(topicId);
        }
        else
        {
            topic.Subscribe();
        }

        Assert.That(topic.IsSubscribed, Is.True);
        receive(publish);
        Assert.That(verified, Is.EqualTo(1), "retired messages do not enter the seen cache, and rejoining restores validation");
    }

    /// <summary>A mesh peer whose gossip this node consumes without accepting keeps its mesh place and is not graylisted.</summary>
    /// <remarks>The node returns Ignored for consumed gossip, which libp2p does not count as a mesh delivery; its default P3 would prune the peer.</remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task Mesh_peer_that_delivers_no_accepted_message_stays_in_the_mesh(CancellationToken token)
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        using PubsubRouter pubsub = new(new PeerStore(), p2p.PubsubSettingsForTest);
        int verified = 0;
        pubsub.VerifyMessage = (_, _) => { verified++; return MessageValidity.Ignored; };
        string topicId = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconBlock);
        pubsub.GetTopic(topicId);
        (PeerId peer, _, Action<Rpc> receive) = ConnectSubscribedPeer(pubsub, topicId);
        await pubsub.Heartbeat();
        Assert.That(((IRoutingStateContainer)pubsub).Mesh[topicId], Does.Contain(peer), "the heartbeat grafts the subscribed peer");

        // The library's default MeshMessageDeliveriesActivation is 5 s of mesh time.
        await Task.Delay(TimeSpan.FromSeconds(6), token);
        await pubsub.Heartbeat();
        Rpc publish = new();
        publish.Publish.Add(new Message { Topic = topicId, Data = ByteString.CopyFrom([1]) });
        receive(publish);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((IRoutingStateContainer)pubsub).Mesh[topicId], Does.Contain(peer), "no delivery score prunes the peer");
            Assert.That(verified, Is.EqualTo(1), "the peer's messages still reach the validator");
        }
    }

    /// <summary>Registers a connected peer subscribed to <paramref name="topicId"/> with <paramref name="pubsub"/>.</summary>
    /// <returns>The peer, the RPCs the router sends it, and a callback that hands the router an RPC from it.</returns>
    private static (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) ConnectSubscribedPeer(PubsubRouter pubsub, string topicId)
    {
        PeerId peer = new Identity(privateKey: null, Nethermind.Libp2p.Core.Dto.KeyType.Secp256K1).PeerId;
        List<Rpc> sent = [];
        TaskCompletionSource dial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(PubsubRouter).GetMethod("OutboundConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pubsub,
            [Multiaddress.Decode($"/ip4/127.0.0.1/tcp/9000/p2p/{peer}"), "/meshsub/1.1.0", dial.Task, (Action<Rpc>)sent.Add]);
        MethodInfo onRpc = typeof(PubsubRouter).GetMethod("OnRpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Receive(Rpc rpc) => onRpc.Invoke(pubsub, [peer, rpc, null, true]);
        Rpc subscribe = new();
        subscribe.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = topicId, Subscribe = true });
        Receive(subscribe);
        return (peer, sent, Receive);
    }

    /// <summary>The router's RPC handler changes the peer sets under its monitor while unsubscribing enumerates them.</summary>
    [Test]
    public void Retiring_a_topic_waits_for_the_router_monitor()
    {
        using PubsubRouter pubsub = new(new PeerStore(), new PubsubSettings());
        GossipTopicSubscriptions subscriptions = new(pubsub, static _ => MessageValidity.Ignored);
        ITopic topic = subscriptions.GetTopic(GossipTopics.Topic(ForkDigest.Compute(Spec, 0), GossipTopics.BeaconBlock));
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task retire;
        lock (pubsub)
        {
            retire = Task.Run(() =>
            {
                started.SetResult();
                topic.Unsubscribe();
            });
            Assert.That(started.Task.Wait(5_000), Is.True, "the unsubscribe worker did not start");
            Assert.That(retire.Wait(200), Is.False, "unsubscribing ran while the router's monitor was held");
        }

        Assert.That(retire.Wait(5_000), Is.True, "unsubscribing did not finish after releasing the monitor");
        Assert.That(topic.IsSubscribed, Is.False);
    }

    private static GossipRouter CreateRouter(double secondsIntoSlot = 6.0)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(secondsIntoSlot);
        return new GossipRouter(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance);
    }

    private static byte[] BlockMessage(ulong slot) => Snappy.CompressToArray(SignedBeaconBlock.Encode(TestChain.CreateBlock(slot, Hash256.Zero)));

    [Test]
    public void Valid_messages_raise_typed_events_with_round_tripped_content()
    {
        GossipRouter router = CreateRouter();
        List<byte[]> received = [];
        router.BeaconBlockReceived += b => received.Add(SignedBeaconBlockCodec.Encode(b, Spec));
        router.AggregateAndProofReceived += a => received.Add(SignedAggregateAndProof.Encode(a));
        router.AttesterSlashingReceived += s => received.Add(AttesterSlashing.Encode(s));

        byte[][] payloads =
        [
            SignedBeaconBlock.Encode(TestChain.CreateBlock(CurrentSlot, Hash256.Zero)),
            SignedAggregateAndProof.Encode(CreateAggregate(CurrentSlot)),
            AttesterSlashing.Encode(new AttesterSlashing { Attestation1 = CreateIndexedAttestation(1, 4), Attestation2 = CreateIndexedAttestation(2, 3) }),
        ];

        string[] names = GossipTopics.SubscribedTopicNames;
        for (int i = 0; i < names.Length; i++)
        {
            router.HandlerFor(names[i])(Snappy.CompressToArray(payloads[i]));
        }

        Assert.That(received, Is.EqualTo(payloads), "every message decodes and round-trips through its typed event");
    }

    private static IEnumerable<TestCaseData> DroppedBlockMessageCases()
    {
        yield return new TestCaseData(Bytes.FromHexString("0x8080c0051068656c6c6f"), GossipDropReason.Oversized)
            .SetName("declared uncompressed length of 11 MiB");
        yield return new TestCaseData(Bytes.FromHexString("0xffffffff"), GossipDropReason.InvalidSnappy)
            .SetName("corrupt snappy data");
        yield return new TestCaseData(Snappy.CompressToArray([1, 2, 3]), GossipDropReason.InvalidSsz)
            .SetName("payload that is not a valid SSZ block");
        yield return new TestCaseData(BlockMessage(CurrentSlot + 2), GossipDropReason.FutureSlot)
            .SetName("slot two ahead of the wall clock");
    }

    [TestCaseSource(nameof(DroppedBlockMessageCases))]
    public void Invalid_block_messages_are_dropped_and_counted(byte[] message, GossipDropReason reason)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.BeaconBlockReceived += _ => received++;

        router.HandleBeaconBlock(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.Zero, "no event for a dropped message");
            Assert.That(router.GetDropCount(reason), Is.EqualTo(1), "the drop is counted under its reason");
        }
    }

    [Test]
    public void Block_older_than_one_epoch_above_finality_is_raised()
    {
        ManualTimestamper time = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot));
        BeaconChainStatusHolder status = new(Spec, time)
        {
            CurrentStatus = new StatusMessageV2 { FinalizedEpoch = Spec.GetEpoch(CurrentSlot) - 2, FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero },
        };
        GossipRouter router = new(Spec, new SlotClock(Spec, time), LimboLogs.Instance, status: status);
        int received = 0;
        router.BeaconBlockReceived += _ => received++;

        router.HandleBeaconBlock(BlockMessage(CurrentSlot - Spec.SlotsPerEpoch - 1));

        Assert.That(received, Is.EqualTo(1));
    }

    [Test]
    public void Slot_boundaries_of_the_basic_sanity_checks_are_inclusive()
    {
        // 11.7 s into the slot leaves 300 ms to the next slot, within MAXIMUM_GOSSIP_CLOCK_DISPARITY.
        GossipRouter router = CreateRouter(secondsIntoSlot: 11.7);
        int received = 0;
        router.BeaconBlockReceived += _ => received++;

        router.HandleBeaconBlock(BlockMessage(CurrentSlot + 1));
        router.HandleBeaconBlock(BlockMessage(CurrentSlot - Spec.SlotsPerEpoch));

        Assert.That(received, Is.EqualTo(2), "the next slot within disparity and an exactly one-epoch-old block are accepted");
    }

    [Test]
    public void Duplicate_messages_are_dropped_and_counted()
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.BeaconBlockReceived += _ => received++;
        byte[] message = BlockMessage(CurrentSlot);

        router.HandleBeaconBlock(message);
        router.HandleBeaconBlock(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.EqualTo(1), "only the first copy raises the event");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1));
        }
    }

    [Test]
    public void Start_subscribes_all_topics_and_rotation_moves_them_to_the_new_digest()
    {
        byte[] bpo1Digest = ForkDigest.Compute(Spec, 412_672);
        byte[] bpo2Digest = ForkDigest.Compute(Spec, 419_072);
        Dictionary<string, FakeTopic> topics = [];
        GossipRouter router = CreateRouter();
        int blocks = 0;
        router.BeaconBlockReceived += _ => blocks++;

        Assert.That(() => router.SubscribeDigest(bpo2Digest), Throws.InvalidOperationException, "rotation requires Start");

        router.Start(id => topics[id] = new FakeTopic(), bpo1Digest);

        List<string> expectedTopics = [];
        foreach (string name in GossipTopics.SubscribedTopicNames)
        {
            expectedTopics.Add(GossipTopics.Topic(bpo1Digest, name));
        }

        Assert.That(topics.Keys, Is.EquivalentTo(expectedTopics), "all gossip topics subscribed for the starting digest");

        FakeTopic blockTopicBpo1 = topics[GossipTopics.Topic(bpo1Digest, GossipTopics.BeaconBlock)];
        blockTopicBpo1.Deliver(BlockMessage(CurrentSlot));
        Assert.That(blocks, Is.EqualTo(1), "messages on a subscribed topic reach the event");

        router.SubscribeDigest(bpo2Digest);
        router.UnsubscribeDigest(bpo1Digest);
        FakeTopic blockTopicBpo2 = topics[GossipTopics.Topic(bpo2Digest, GossipTopics.BeaconBlock)];
        blockTopicBpo1.Deliver(BlockMessage(CurrentSlot - 1));
        blockTopicBpo2.Deliver(BlockMessage(CurrentSlot - 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTopicBpo1.IsSubscribed, Is.False, "old topics are unsubscribed on rotation");
            Assert.That(blockTopicBpo1.HasHandlers, Is.False, "old handlers are detached on rotation");
            Assert.That(blockTopicBpo2.IsSubscribed, "new topics are subscribed on rotation");
            Assert.That(blocks, Is.EqualTo(2), "only the new digest topic delivers after rotation");
        }
    }

    [Test]
    public void Gloas_topics_are_subscribed_only_on_a_Gloas_digest_and_survive_digest_rotation()
    {
        BeaconChainSpec spec = GossipDigestWindowTests.WithBlobEntry(Sepolia, new BlobScheduleEntry(Sepolia.GloasForkEpoch + 1, 21));
        byte[] fuluDigest = ForkDigest.Compute(spec, spec.GloasForkEpoch - 1);
        byte[] bpo1Digest = ForkDigest.Compute(spec, spec.GloasForkEpoch);
        byte[] bpo2Digest = ForkDigest.Compute(spec, spec.GloasForkEpoch + 1);
        Dictionary<string, FakeTopic> topics = [];
        GossipRouter router = new(spec, new SlotClock(spec, new ManualTimestamper(SepoliaSlotStart(FirstGloasSlot + 1).AddSeconds(6))), LimboLogs.Instance);
        int envelopes = 0;
        router.ExecutionPayloadEnvelopeReceived += _ => envelopes++;

        router.Start(id => topics[id] = new FakeTopic(), fuluDigest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topics.Keys, Has.None.Contain(GossipTopics.ExecutionPayload), "Gloas topics are not part of the fixed pre-Gloas set");
            Assert.That(topics.Keys, Has.None.Contain(GossipTopics.PayloadAttestationMessage));
        }

        router.SubscribeDigest(bpo1Digest);
        string envelopeTopicBpo1 = GossipTopics.Topic(bpo1Digest, GossipTopics.ExecutionPayload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topics.Keys, Does.Contain(envelopeTopicBpo1));
            Assert.That(topics.Keys, Does.Contain(GossipTopics.Topic(bpo1Digest, GossipTopics.PayloadAttestationMessage)), "fork choice consumes PTC votes from gossip");
        }

        // A pre-Gloas slot, so the envelope handler drops each delivery as one.
        topics[envelopeTopicBpo1].Deliver(Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(PreGloasEnvelope())));
        Assert.That(router.GetDropCount(GossipDropReason.InvalidField), Is.EqualTo(1), "Gloas topics deliver to their handlers");

        // A second subscription must not double-subscribe.
        router.SubscribeDigest(bpo1Digest);
        Assert.That(topics.Keys.Count(k => k == envelopeTopicBpo1), Is.EqualTo(1));

        router.SubscribeDigest(bpo2Digest);
        router.UnsubscribeDigest(bpo1Digest);
        string envelopeTopicBpo2 = GossipTopics.Topic(bpo2Digest, GossipTopics.ExecutionPayload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topics[envelopeTopicBpo1].IsSubscribed, Is.False, "old Gloas topics are unsubscribed on rotation");
            Assert.That(topics.Keys, Does.Contain(envelopeTopicBpo2), "Gloas topics rotate to the new digest automatically");
        }

        // A distinct builder index, so the message differs from the BPO1 delivery and is not
        // suppressed as a duplicate of it (dedup keys on topic name + payload, not the digest).
        topics[envelopeTopicBpo2].Deliver(Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(PreGloasEnvelope(builderIndex: 4))));
        Assert.That((router.GetDropCount(GossipDropReason.InvalidField), envelopes), Is.EqualTo((2L, 0)), "the rotated Gloas topic still delivers");
    }

    public enum EnvelopeCase
    {
        Valid,
        RequestsOverLimit,
        SeenForBlockAndBuilder,
        SeenForBlockFromAnotherBuilder,
        PreGloasSlotNotHeld,
        PreGloasSlotForHeldGloasBlock,
        PreGloasSlotBeforeFinalized,
        BlockNotHeld,
        BlockFailedValidation,
        NoStore,
        BlockNotGloas,
        StoredBlockUnreadable,
        BeforeFinalized,
        PayloadBeforeFinalizedForLaterBlock,
        BlockBeforeFinalizedForLaterPayload,
        AtFinalizedStartSlot,
        SlotMismatch,
        BuilderIndexMismatch,
        BlockHashMismatch,
        ExecutionRequestsRootMismatch,
        TooManyWithdrawals,
        MaxWithdrawals,
    }

    // validate_execution_payload_envelope_gossip (gloas/p2p-interface.md), in the router's order; an envelope that passes, or whose
    // block is not held yet, is raised for the import pipeline, which checks the signature.
    [TestCase(EnvelopeCase.RequestsOverLimit, MessageValidity.Rejected, GossipDropReason.LimitExceeded, false)]
    [TestCase(EnvelopeCase.SeenForBlockAndBuilder, MessageValidity.Ignored, GossipDropReason.Duplicate, false)]
    [TestCase(EnvelopeCase.SeenForBlockFromAnotherBuilder, MessageValidity.Ignored, null, true)]
    [TestCase(EnvelopeCase.PreGloasSlotNotHeld, MessageValidity.Ignored, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.PreGloasSlotForHeldGloasBlock, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.PreGloasSlotBeforeFinalized, MessageValidity.Ignored, GossipDropReason.BeforeFinalized, false)]
    [TestCase(EnvelopeCase.BlockNotHeld, MessageValidity.Ignored, null, true)]
    [TestCase(EnvelopeCase.BlockFailedValidation, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.NoStore, MessageValidity.Ignored, null, true)]
    [TestCase(EnvelopeCase.BlockNotGloas, MessageValidity.Ignored, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.StoredBlockUnreadable, MessageValidity.Ignored, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.BeforeFinalized, MessageValidity.Ignored, GossipDropReason.BeforeFinalized, false)]
    [TestCase(EnvelopeCase.PayloadBeforeFinalizedForLaterBlock, MessageValidity.Ignored, GossipDropReason.BeforeFinalized, false)]
    [TestCase(EnvelopeCase.BlockBeforeFinalizedForLaterPayload, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.AtFinalizedStartSlot, MessageValidity.Ignored, null, true)]
    [TestCase(EnvelopeCase.SlotMismatch, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.BuilderIndexMismatch, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.BlockHashMismatch, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.ExecutionRequestsRootMismatch, MessageValidity.Rejected, GossipDropReason.InvalidField, false)]
    [TestCase(EnvelopeCase.TooManyWithdrawals, MessageValidity.Rejected, GossipDropReason.LimitExceeded, false)]
    [TestCase(EnvelopeCase.MaxWithdrawals, MessageValidity.Ignored, null, true)]
    [TestCase(EnvelopeCase.Valid, MessageValidity.Ignored, null, true)]
    public void Envelope_verdict_and_consumption_follow_the_gossip_rules(EnvelopeCase testCase, MessageValidity expected, GossipDropReason? reason, bool raised)
    {
        // Finality before the fork leaves a pre-Gloas payload slot above the finalized slot, so only the slot match can refuse it.
        EnvelopeFixture fixture = new(withStore: testCase != EnvelopeCase.NoStore,
            finalizedEpoch: testCase is EnvelopeCase.PreGloasSlotNotHeld or EnvelopeCase.PreGloasSlotForHeldGloasBlock ? Sepolia.GloasForkEpoch - 1 : null);
        SignedExecutionPayloadEnvelope envelope = fixture.Envelope();
        ExecutionPayloadEnvelope message = envelope.Message!;
        switch (testCase)
        {
            case EnvelopeCase.RequestsOverLimit:
                message.ExecutionRequests = new ExecutionRequestsGloas
                {
                    BuilderExits = [.. Enumerable.Range(0, Presets.MaxBuilderExitRequestsPerPayload + 1).Select(static _ => new BuilderExitRequest { SourceAddress = Address.Zero, Pubkey = new BlsPublicKey(new byte[BlsPublicKey.Length]) })],
                };
                break;
            case EnvelopeCase.SeenForBlockAndBuilder:
                fixture.Router.MarkEnvelopeSeen(fixture.BlockRoot, EnvelopeFixture.BuilderIndex);
                break;
            case EnvelopeCase.SeenForBlockFromAnotherBuilder:
                fixture.Router.MarkEnvelopeSeen(fixture.BlockRoot, EnvelopeFixture.BuilderIndex + 1);
                break;
            case EnvelopeCase.PreGloasSlotNotHeld:
                message.BeaconBlockRoot = Keccak.OfAnEmptyString;
                message.Payload!.SlotNumber = FirstGloasSlot - 1;
                break;
            case EnvelopeCase.PreGloasSlotForHeldGloasBlock or EnvelopeCase.PreGloasSlotBeforeFinalized:
                message.Payload!.SlotNumber = FirstGloasSlot - 1;
                break;
            case EnvelopeCase.BlockNotHeld:
                message.BeaconBlockRoot = Keccak.OfAnEmptyString;
                break;
            case EnvelopeCase.BlockFailedValidation:
                message.BeaconBlockRoot = Keccak.OfAnEmptyString;
                fixture.FailedBlocks.Add(message.BeaconBlockRoot, message.Payload!.SlotNumber);
                break;
            case EnvelopeCase.BlockNotGloas:
                message.BeaconBlockRoot = fixture.FuluRoot;
                break;
            case EnvelopeCase.StoredBlockUnreadable:
                fixture.CorruptBlock(fixture.BlockRoot);
                break;
            case EnvelopeCase.BeforeFinalized:
                message.BeaconBlockRoot = fixture.FinalizedRoot;
                message.Payload!.SlotNumber = EnvelopeFixture.FinalizedBlockSlot;
                break;
            case EnvelopeCase.PayloadBeforeFinalizedForLaterBlock:
                message.Payload!.SlotNumber = EnvelopeFixture.FinalizedBlockSlot;
                break;
            case EnvelopeCase.BlockBeforeFinalizedForLaterPayload:
                message.BeaconBlockRoot = fixture.FinalizedRoot;
                break;
            case EnvelopeCase.AtFinalizedStartSlot:
                message.BeaconBlockRoot = fixture.FinalizedStartRoot;
                message.Payload!.SlotNumber = EnvelopeFixture.FinalizedStartSlot;
                break;
            case EnvelopeCase.SlotMismatch:
                message.Payload!.SlotNumber = EnvelopeFixture.WallSlot - 1;
                break;
            case EnvelopeCase.BuilderIndexMismatch:
                message.BuilderIndex = EnvelopeFixture.BuilderIndex + 1;
                break;
            case EnvelopeCase.BlockHashMismatch:
                message.Payload!.BlockHash = Keccak.OfAnEmptyString;
                break;
            case EnvelopeCase.ExecutionRequestsRootMismatch:
                message.ExecutionRequests = new ExecutionRequestsGloas
                {
                    Withdrawals = [new WithdrawalRequest { SourceAddress = Address.Zero, ValidatorPubkey = new BlsPublicKey(new byte[BlsPublicKey.Length]), Amount = 1 }],
                };
                break;
            case EnvelopeCase.TooManyWithdrawals or EnvelopeCase.MaxWithdrawals:
                int withdrawals = Presets.MaxWithdrawalsPerPayload + (testCase == EnvelopeCase.TooManyWithdrawals ? 1 : 0);
                message.Payload!.Withdrawals = [.. Enumerable.Range(0, withdrawals).Select(static i => new Nethermind.BeaconChain.Types.Withdrawal { Index = (ulong)i, Address = Address.Zero })];
                break;
        }

        MessageValidity validity = fixture.Handle(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected), "validity");
            Assert.That(fixture.Raised, Is.EqualTo(raised ? 1 : 0), "consumed");
            Assert.That(Enum.GetValues<GossipDropReason>().Sum(fixture.Router.GetDropCount), Is.EqualTo(reason is null ? 0 : 1), "one drop at most");
            if (reason is { } expectedReason)
            {
                Assert.That(fixture.Router.GetDropCount(expectedReason), Is.EqualTo(1), "drop reason");
            }
        }
    }

    [Test]
    public void Envelope_for_a_block_not_held_is_checked_against_its_bid_once_the_block_is_stored()
    {
        EnvelopeFixture fixture = new(withStore: true);
        Hash256 lateRoot = fixture.PutGloasBlock(EnvelopeFixture.WallSlot, parentRoot: Keccak.OfAnEmptyString, store: false);

        MessageValidity early = fixture.Handle(fixture.Envelope(lateRoot));
        fixture.PutGloasBlock(EnvelopeFixture.WallSlot, parentRoot: Keccak.OfAnEmptyString, store: true);
        MessageValidity late = fixture.Handle(fixture.Envelope(lateRoot, builderIndex: EnvelopeFixture.BuilderIndex + 1));

        Assert.That((early, fixture.Raised, late), Is.EqualTo((MessageValidity.Ignored, 1, MessageValidity.Rejected)),
            "an unheld root is consumed and not cached, so the stored block's bid decides the next envelope");
    }

    [Test]
    public void Held_block_is_decoded_once_for_every_envelope_naming_it()
    {
        EnvelopeFixture fixture = new(withStore: true);
        fixture.Handle(fixture.Envelope());
        long reads = fixture.BlockReads;

        fixture.Handle(fixture.Envelope(builderIndex: EnvelopeFixture.BuilderIndex + 1));
        fixture.Handle(fixture.Envelope(fixture.FuluRoot));
        fixture.Handle(fixture.Envelope(fixture.FuluRoot, builderIndex: 7));
        long afterFirstFulu = fixture.BlockReads;
        fixture.Handle(fixture.Envelope(fixture.FuluRoot, builderIndex: 8));
        (long afterFulu, int raised) = (fixture.BlockReads, fixture.Raised);
        Hash256 unreadableRoot = fixture.PutGloasBlock(EnvelopeFixture.WallSlot, parentRoot: Keccak.OfAnEmptyString);
        fixture.CorruptBlock(unreadableRoot);
        MessageValidity first = fixture.Handle(fixture.Envelope(unreadableRoot));
        MessageValidity second = fixture.Handle(fixture.Envelope(unreadableRoot, builderIndex: 7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((afterFirstFulu - reads, afterFulu - afterFirstFulu), Is.EqualTo((1L, 0L)),
                "a Gloas bid and a held non-Gloas block are both cached after one read");
            Assert.That((first, second, fixture.BlockReads - afterFulu, fixture.Raised - raised), Is.EqualTo((MessageValidity.Ignored, MessageValidity.Ignored, 1L, 0)),
                "an unreadable stored block is read once, then ignored from the cache without raising");
        }
    }

    [Test]
    public void Uncached_store_decodes_are_bounded_per_slot_except_for_the_canonical_block_at_a_recent_slot([Values(0UL, 1UL)] ulong slotsSinceWallSlot)
    {
        EnvelopeFixture fixture = new(withStore: true);
        ulong now = EnvelopeFixture.WallSlot + slotsSinceWallSlot;
        fixture.Timestamper.Set(SepoliaSlotStart(now));
        Hash256[] roots = [.. Enumerable.Range(0, GossipRouter.StoreDecodesPerSlot + 1).Select(i => fixture.PutGloasBlock(EnvelopeFixture.WallSlot, parentRoot: Keccak.Compute([(byte)i])))];
        fixture.Store.SetCanonicalRoot(EnvelopeFixture.WallSlot, fixture.BlockRoot);

        MessageValidity canonical = fixture.Handle(fixture.Envelope(builderIndex: EnvelopeFixture.BuilderIndex + 1));
        MessageValidity[] verdicts = [.. roots.Select(root => fixture.Handle(fixture.Envelope(root, builderIndex: EnvelopeFixture.BuilderIndex + 1)))];
        fixture.Timestamper.Set(SepoliaSlotStart(now + 1));
        MessageValidity nextSlot = fixture.Handle(fixture.Envelope(roots[^1], builderIndex: EnvelopeFixture.BuilderIndex + 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts[..^1], Is.All.EqualTo(MessageValidity.Rejected), "the whole budget is left for these blocks after the canonical one");
            Assert.That((verdicts[^1], fixture.Router.GetDropCount(GossipDropReason.StoreDecodeBudgetSpent)), Is.EqualTo((MessageValidity.Ignored, 1L)), "one past the budget is not decoded");
            Assert.That(canonical, Is.EqualTo(MessageValidity.Rejected), "the canonical block at the current or previous slot is decoded outside the budget");
            Assert.That(nextSlot, Is.EqualTo(MessageValidity.Rejected), "the budget is renewed each slot");
        }
    }

    private static DateTime SepoliaSlotStart(ulong slot) => DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot);

    /// <summary>A Sepolia router with a store holding a Gloas block at the wall slot, one below the finalized slot, one at the finalized start slot, and a Fulu block.</summary>
    private sealed class EnvelopeFixture
    {
        public const ulong BuilderIndex = 5;
        public static readonly ulong WallSlot = FirstGloasSlot + 3 * Sepolia.SlotsPerEpoch + 1;
        public static readonly ulong FinalizedBlockSlot = FirstGloasSlot + Sepolia.SlotsPerEpoch;
        private static readonly ulong FinalizedEpoch = Sepolia.GloasForkEpoch + 2;
        public static readonly ulong FinalizedStartSlot = FinalizedEpoch * Sepolia.SlotsPerEpoch;
        private static readonly Hash256 BlockHash = Keccak.Compute("payload");

        private readonly MemColumnsDb<BeaconChainDbColumns> _db = new();

        public EnvelopeFixture(bool withStore, ulong? finalizedEpoch = null)
        {
            Store = new BeaconChainStore(_db, Sepolia);
            Timestamper = new ManualTimestamper(SepoliaSlotStart(WallSlot).AddSeconds(6));
            BeaconChainStatusHolder status = new(Sepolia, Timestamper)
            {
                CurrentStatus = new StatusMessageV2 { ForkDigest = SepoliaGloasDigest, FinalizedRoot = Hash256.Zero, FinalizedEpoch = finalizedEpoch ?? FinalizedEpoch, HeadRoot = Hash256.Zero },
            };
            BlockRoot = PutGloasBlock(WallSlot, Hash256.Zero);
            FinalizedRoot = PutGloasBlock(FinalizedBlockSlot, Hash256.Zero);
            FinalizedStartRoot = PutGloasBlock(FinalizedStartSlot, Hash256.Zero);
            SignedBeaconBlock fulu = CreateMinimalBlock(FirstGloasSlot - 1);
            FuluRoot = SszRoots.HashTreeRoot(fulu.Message!);
            Store.PutBlock(FuluRoot, fulu);
            Router = new GossipRouter(Sepolia, new SlotClock(Sepolia, Timestamper), LimboLogs.Instance, withStore ? Store : null, status, FailedBlocks);
            Router.ExecutionPayloadEnvelopeReceived += _ => Raised++;
        }

        public GossipRouter Router { get; }
        public FailedBlockRoots FailedBlocks { get; } = new();
        public BeaconChainStore Store { get; }
        public ManualTimestamper Timestamper { get; }
        public Hash256 BlockRoot { get; }
        public Hash256 FinalizedRoot { get; }
        public Hash256 FinalizedStartRoot { get; }
        public Hash256 FuluRoot { get; }
        public int Raised { get; private set; }
        public long BlockReads => ((MemDb)_db.GetColumnDb(BeaconChainDbColumns.Blocks)).ReadsCount;

        public void CorruptBlock(Hash256 root) => _db.GetColumnDb(BeaconChainDbColumns.Blocks)[root.Bytes] = [0xFF];

        public Hash256 PutGloasBlock(ulong slot, Hash256 parentRoot, bool store = true)
        {
            SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
            ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
            bid.BuilderIndex = BuilderIndex;
            bid.BlockHash = BlockHash;
            bid.ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas());
            Hash256 root = SszRoots.HashTreeRoot(block.Message);
            if (store)
            {
                Store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
            }

            return root;
        }

        public SignedExecutionPayloadEnvelope Envelope(Hash256? blockRoot = null, ulong builderIndex = BuilderIndex) => new()
        {
            Message = new ExecutionPayloadEnvelope
            {
                Payload = new ExecutionPayloadGloas { SlotNumber = WallSlot, BlockHash = BlockHash, Withdrawals = [] },
                ExecutionRequests = new ExecutionRequestsGloas(),
                BuilderIndex = builderIndex,
                BeaconBlockRoot = blockRoot ?? BlockRoot,
                ParentBeaconBlockRoot = Hash256.Zero,
            },
            Signature = new BlsSignature(new byte[BlsSignature.Length]),
        };

        public MessageValidity Handle(SignedExecutionPayloadEnvelope envelope) =>
            Router.Handle(GossipTopics.ExecutionPayload, gloasTopic: true, Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(envelope)));
    }

    private static SignedExecutionPayloadEnvelope PreGloasEnvelope(ulong builderIndex = 3)
    {
        SignedExecutionPayloadEnvelope envelope = CreateEnvelope(builderIndex);
        envelope.Message!.Payload!.SlotNumber = FirstGloasSlot - 1;
        return envelope;
    }

    private static SignedExecutionPayloadEnvelope CreateEnvelope(ulong builderIndex = 3) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = CurrentSlot },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = builderIndex,
            BeaconBlockRoot = Hash256.Zero,
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };

    private static SignedAggregateAndProof CreateAggregate(ulong slot) => new()
    {
        Message = new AggregateAndProof
        {
            AggregatorIndex = 7,
            Aggregate = new Attestation
            {
                AggregationBits = new BitArray(8) { [0] = true },
                Data = new AttestationData
                {
                    Slot = slot,
                    Index = 0,
                    BeaconBlockRoot = Hash256.Zero,
                    Source = new Checkpoint { Epoch = 1, Root = Hash256.Zero },
                    Target = new Checkpoint { Epoch = Spec.GetEpoch(slot), Root = Hash256.Zero },
                },
                CommitteeBits = new BitArray(64) { [0] = true },
            },
        },
    };

    private static IndexedAttestation CreateIndexedAttestation(ulong sourceEpoch, ulong targetEpoch) => new()
    {
        AttestingIndices = [1, 2, 3],
        Data = new AttestationData
        {
            Slot = CurrentSlot,
            Index = 0,
            BeaconBlockRoot = Hash256.Zero,
            Source = new Checkpoint { Epoch = sourceEpoch, Root = Hash256.Zero },
            Target = new Checkpoint { Epoch = targetEpoch, Root = Hash256.Zero },
        },
    };

    private sealed class FakeTopic : ITopic
    {
        private static readonly PeerId DeliveringPeer = new Nethermind.Libp2p.Core.Identity(privateKey: null, Nethermind.Libp2p.Core.Dto.KeyType.Secp256K1).PeerId;

        public event Action<PeerId, byte[]>? OnMessage;

        public bool IsSubscribed { get; private set; }

        public bool HasHandlers => OnMessage is not null;

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }

        public void Deliver(byte[] message) => OnMessage?.Invoke(DeliveringPeer, message);
    }
}
