// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Logging;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// From Gloas the beacon_block topic carries gloas.SignedBeaconBlock (gloas/p2p-interface.md) and the topic's
// fork digest fixes the message type (phase0 p2p: MUST reject messages containing an incorrect type).
public partial class GossipRouterTests
{
    private static readonly byte[] SepoliaFuluDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch - 1);
    private static readonly byte[] SepoliaGloasDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch);

    [Test]
    public void Block_is_raised_only_in_the_shape_of_its_topic_fork([Values] bool gloasTopic, [Values] bool gloasBlock, [Values] bool viaStartDigestOverload)
    {
        GossipRouter router = CreateSepoliaRouter();
        List<ForkedSignedBeaconBlock> received = [];
        router.BeaconBlockReceived += (message, _) => received.Add(message);
        Dictionary<string, FakeTopic> topics = [];
        byte[] digest = gloasTopic ? SepoliaGloasDigest : SepoliaFuluDigest;
        router.Start(id => topics[id] = new FakeTopic(), digest);

        if (viaStartDigestOverload)
        {
            router.HandleBeaconBlock(SepoliaBlockMessage(gloasBlock));
        }
        else
        {
            router.Handle(GossipTopics.BeaconBlock, gloasTopic, SepoliaBlockMessage(gloasBlock));
        }

        if (gloasTopic == gloasBlock)
        {
            Assert.That(received, Has.Count.EqualTo(1).And.All.TypeOf(gloasBlock ? typeof(ForkedSignedBeaconBlock.OfGloas) : typeof(ForkedSignedBeaconBlock.OfFulu)));
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(received, Is.Empty, "a block of the other fork's type is not raised");
                Assert.That(router.GetDropCount(GossipDropReason.InvalidSsz), Is.EqualTo(1), "the wrong type is dropped as undecodable for the topic");
            }
        }
    }

    [Test]
    public void Handler_keeps_the_fork_of_the_digest_it_was_subscribed_under_after_rotation()
    {
        GossipRouter router = CreateSepoliaRouter();
        List<ForkedSignedBeaconBlock> received = [];
        router.BeaconBlockReceived += (message, _) => received.Add(message);
        Dictionary<string, FakeTopic> topics = [];
        router.Start(id => topics[id] = new FakeTopic(), SepoliaFuluDigest);
        Action<byte[]> fuluTopicHandler = router.HandlerFor(GossipTopics.BeaconBlock);

        router.SubscribeDigest(SepoliaGloasDigest);
        router.UnsubscribeDigest(SepoliaFuluDigest);
        fuluTopicHandler(SepoliaBlockMessage(gloas: false));
        router.Handle(GossipTopics.BeaconBlock, gloasTopic: true, SepoliaBlockMessage(gloas: true));

        Assert.That(received.Select(static b => b.GetType()), Is.EqualTo(new[] { typeof(ForkedSignedBeaconBlock.OfFulu), typeof(ForkedSignedBeaconBlock.OfGloas) }),
            "a Fulu-topic message delivered after rotation stays Fulu, and the current topic is Gloas");
    }

    // One slot into the Gloas fork, so both the last Fulu slot and the first Gloas slot pass slot sanity.
    private static GossipRouter CreateSepoliaRouter()
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + (FirstGloasSlot + 1) * Sepolia.SecondsPerSlot).AddSeconds(6);
        return new GossipRouter(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(now)), LimboLogs.Instance);
    }

    private static byte[] SepoliaBlockMessage(bool gloas) => Snappy.CompressToArray(gloas
        ? SignedBeaconBlockGloas.Encode(CreateMinimalGloasBlock(FirstGloasSlot))
        : SignedBeaconBlock.Encode(SepoliaFuluBlock(FirstGloasSlot - 1)));

    // bellatrix p2p: a Fulu block's payload timestamp must be compute_time_at_slot to be consumed.
    private static SignedBeaconBlock SepoliaFuluBlock(ulong slot)
    {
        SignedBeaconBlock block = CreateMinimalBlock(slot);
        block.Message!.Body!.ExecutionPayload!.Timestamp = Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot;
        return block;
    }
}
