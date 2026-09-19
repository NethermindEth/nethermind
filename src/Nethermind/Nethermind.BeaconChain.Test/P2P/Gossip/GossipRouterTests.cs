// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class GossipRouterTests
{
    // A Fulu/BPO2-era mainnet slot so messages exercise the current digest configuration.
    private const ulong CurrentSlot = 13_410_304;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

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
        router.BeaconBlockReceived += b => received.Add(SignedBeaconBlock.Encode(b));
        router.AggregateAndProofReceived += a => received.Add(SignedAggregateAndProof.Encode(a));
        router.VoluntaryExitReceived += e => received.Add(SignedVoluntaryExit.Encode(e));
        router.ProposerSlashingReceived += s => received.Add(ProposerSlashing.Encode(s));
        router.AttesterSlashingReceived += s => received.Add(AttesterSlashing.Encode(s));

        byte[][] payloads =
        [
            SignedBeaconBlock.Encode(TestChain.CreateBlock(CurrentSlot, Hash256.Zero)),
            SignedAggregateAndProof.Encode(CreateAggregate(CurrentSlot)),
            SignedVoluntaryExit.Encode(new SignedVoluntaryExit { Message = new VoluntaryExit { Epoch = 1, ValidatorIndex = 2 } }),
            ProposerSlashing.Encode(new ProposerSlashing { SignedHeader1 = CreateHeader(1), SignedHeader2 = CreateHeader(2) }),
            AttesterSlashing.Encode(new AttesterSlashing { Attestation1 = CreateIndexedAttestation(1), Attestation2 = CreateIndexedAttestation(2) }),
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
        yield return new TestCaseData(BlockMessage(CurrentSlot + 1), GossipDropReason.FutureSlot)
            .SetName("next slot when its start is beyond the clock disparity");
        yield return new TestCaseData(BlockMessage(CurrentSlot - Spec.SlotsPerEpoch - 1), GossipDropReason.StaleSlot)
            .SetName("block older than one epoch");
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

        Assert.That(() => router.RotateDigest(bpo2Digest), Throws.InvalidOperationException, "rotation requires Start");

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

        router.RotateDigest(bpo2Digest);
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

    private static SignedAggregateAndProof CreateAggregate(ulong slot) => new()
    {
        Message = new AggregateAndProof
        {
            AggregatorIndex = 7,
            Aggregate = new Attestation
            {
                AggregationBits = new BitArray(8),
                Data = new AttestationData
                {
                    Slot = slot,
                    Index = 0,
                    BeaconBlockRoot = Hash256.Zero,
                    Source = new Checkpoint { Epoch = 1, Root = Hash256.Zero },
                    Target = new Checkpoint { Epoch = 2, Root = Hash256.Zero },
                },
                CommitteeBits = new BitArray(64),
            },
        },
    };

    private static SignedBeaconBlockHeader CreateHeader(ulong proposerIndex) => new()
    {
        Message = new BeaconBlockHeader
        {
            Slot = CurrentSlot,
            ProposerIndex = proposerIndex,
            ParentRoot = Hash256.Zero,
            StateRoot = Hash256.Zero,
            BodyRoot = Hash256.Zero,
        },
    };

    private static IndexedAttestation CreateIndexedAttestation(ulong epoch) => new()
    {
        AttestingIndices = [1, 2, 3],
        Data = new AttestationData
        {
            Slot = CurrentSlot,
            Index = 0,
            BeaconBlockRoot = Hash256.Zero,
            Source = new Checkpoint { Epoch = epoch, Root = Hash256.Zero },
            Target = new Checkpoint { Epoch = epoch + 1, Root = Hash256.Zero },
        },
    };

    private sealed class FakeTopic : ITopic
    {
        public event Action<byte[]>? OnMessage;

        public bool IsSubscribed { get; private set; }

        public bool HasHandlers => OnMessage is not null;

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }

        public void Deliver(byte[] message) => OnMessage?.Invoke(message);
    }
}
