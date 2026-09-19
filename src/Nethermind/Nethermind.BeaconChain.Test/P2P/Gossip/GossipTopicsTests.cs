// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class GossipTopicsTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    // 419072 is the mainnet Fulu BPO2 epoch, whose EIP-7892-masked digest is 8c9f62fe.
    [TestCase(GossipTopics.BeaconBlock, "/eth2/8c9f62fe/beacon_block/ssz_snappy")]
    [TestCase(GossipTopics.BeaconAggregateAndProof, "/eth2/8c9f62fe/beacon_aggregate_and_proof/ssz_snappy")]
    [TestCase(GossipTopics.VoluntaryExit, "/eth2/8c9f62fe/voluntary_exit/ssz_snappy")]
    [TestCase(GossipTopics.ProposerSlashing, "/eth2/8c9f62fe/proposer_slashing/ssz_snappy")]
    [TestCase(GossipTopics.AttesterSlashing, "/eth2/8c9f62fe/attester_slashing/ssz_snappy")]
    public void Builds_topic_strings_for_the_current_mainnet_digest(string name, string expected) =>
        Assert.That(GossipTopics.Topic(GossipTopics.CurrentDigest(Spec, 419072), name), Is.EqualTo(expected));

    [Test]
    public void Rotation_schedule_covers_fork_and_bpo_epochs()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GossipTopics.DigestRotationEpochs(Spec, 0),
                Is.EqualTo(new ulong[] { 0, 74240, 144896, 194048, 269568, 364032, 411392, 412672, 419072 }),
                "all fork activations plus the BPO schedule");
            Assert.That(GossipTopics.DigestRotationEpochs(Spec, 412_000),
                Is.EqualTo(new ulong[] { 412672, 419072 }),
                "only upcoming rotations from a mid-Fulu epoch");
            Assert.That(GossipTopics.DigestRotationEpochs(Spec, 412_672),
                Is.EqualTo(new ulong[] { 412672, 419072 }),
                "a rotation epoch itself is included");
        }
    }

    [Test]
    public void Next_rotation_returns_the_next_digest_or_null_when_none_is_scheduled()
    {
        (ulong epoch, byte[] digest) = GossipTopics.NextRotation(Spec, 412_672)!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(epoch, Is.EqualTo(419_072ul), "BPO2 follows BPO1");
            Assert.That(digest, Is.EqualTo(Bytes.FromHexString("0x8c9f62fe")), "BPO2 digest");
            Assert.That(GossipTopics.NextRotation(Spec, 419_072), Is.Null, "nothing scheduled after BPO2");
        }
    }
}
