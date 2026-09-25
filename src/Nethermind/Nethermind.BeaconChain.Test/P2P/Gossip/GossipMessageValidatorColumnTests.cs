// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// Only Accepted messages reach the topic events, so a Fulu sidecar that passes is consumed by the validator and never
// forwarded before its proposer checks; a Gloas sidecar needs no state and is Accepted, so it is re-broadcast.
public class GossipMessageValidatorColumnTests
{
    private const ulong Subnet = 5;
    private const ulong MainnetSlot = 13_410_304;
    private static readonly BeaconChainSpec Mainnet = BeaconChainSpec.Mainnet;
    private static readonly byte[] MainnetDigest = ForkDigest.Compute(Mainnet, Mainnet.GetEpoch(MainnetSlot));

    private static IEnumerable<TestCaseData> FuluCases()
    {
        yield return Fulu("valid sidecar is consumed and not forwarded", Sidecar(), 0, MessageValidity.Ignored, consumed: true, null);
        yield return Fulu("no commitments", Sidecar(static s => s.KzgCommitments = []), 0, MessageValidity.Rejected, consumed: false, ColumnGossipDropReason.FailedStructure);
        int overBlobLimit = (int)Mainnet.GetBlobParameters(Mainnet.GetEpoch(MainnetSlot))!.Value.MaxBlobsPerBlock + 1;
        yield return Fulu("more commitments than the epoch's blob limit", Sidecar(s => Widen(s, overBlobLimit)), 0, MessageValidity.Rejected, consumed: false, ColumnGossipDropReason.FailedBlobCount);
        yield return Fulu("wrong subnet", DataColumnSidecarTestFixture.BuildValidSidecar(Subnet + 1, MainnetSlot), 0, MessageValidity.Rejected, consumed: false, ColumnGossipDropReason.WrongSubnet);
        yield return Fulu("future slot", DataColumnSidecarTestFixture.BuildValidSidecar(Subnet, MainnetSlot + 2), 0, MessageValidity.Ignored, consumed: false, ColumnGossipDropReason.FutureSlot);
        yield return Fulu("at the finalized start slot", Sidecar(), Mainnet.GetEpoch(MainnetSlot), MessageValidity.Ignored, consumed: false, ColumnGossipDropReason.BeforeFinalized);
        yield return Fulu("inclusion proof is ordered after the parent checks", Sidecar(static s => s.KzgCommitmentsInclusionProof![0] = Hash256.Zero), 0, MessageValidity.Ignored, consumed: false, ColumnGossipDropReason.FailedInclusionProof);
        yield return Fulu("KZG proofs are ordered after the parent checks", Sidecar(static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]]), 0, MessageValidity.Ignored, consumed: false, ColumnGossipDropReason.FailedKzgProofs);
    }

    [TestCaseSource(nameof(FuluCases))]
    public void Fulu_sidecar_is_consumed_or_dropped_per_the_spec_order(DataColumnSidecar sidecar, ulong finalizedEpoch, MessageValidity expected, bool consumed, ColumnGossipDropReason? reason)
    {
        (GossipMessageValidator validator, _, ColumnGossipRouter columns, DataColumnSidecarPool pool) = CreateMainnet(finalizedEpoch);
        int raised = 0;
        columns.DataColumnSidecarReceived += _ => raised++;

        MessageValidity validity = validator.Verify(Message(GossipTopics.Topic(MainnetDigest, GossipTopics.DataColumnSidecarTopicName(Subnet)), Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected));
            Assert.That(raised, Is.EqualTo(consumed ? 1 : 0), "consumed");
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!), sidecar.Index, out _), Is.EqualTo(consumed), "pooled");
            if (reason is { } dropReason)
            {
                Assert.That(columns.GetDropCount(dropReason), Is.EqualTo(1), "the drop is counted under its reason");
            }
        }
    }

    [TestCase("data_column_sidecar_128", TestName = "subnet past DATA_COLUMN_SIDECAR_SUBNET_COUNT is an unknown topic")]
    [TestCase("data_column_sidecar_99999999999999999999", TestName = "subnet overflowing a ulong is an unknown topic")]
    public void Column_topic_outside_the_subnet_range_is_rejected(string name)
    {
        (GossipMessageValidator validator, GossipRouter gossip, _, _) = CreateMainnet();

        MessageValidity validity = validator.Verify(Message(GossipTopics.Topic(MainnetDigest, name), Snappy.CompressToArray(DataColumnSidecar.Encode(Sidecar()))));

        Assert.That((validity, gossip.GetDropCount(GossipDropReason.UnknownTopic)), Is.EqualTo((MessageValidity.Rejected, 1L)));
    }

    [Test]
    public void Column_on_a_digest_that_is_not_current_is_ignored()
    {
        (GossipMessageValidator validator, GossipRouter gossip, ColumnGossipRouter columns, _) = CreateMainnet();
        int raised = 0;
        columns.DataColumnSidecarReceived += _ => raised++;

        MessageValidity validity = validator.Verify(Message(GossipTopics.Topic(ForkDigest.Compute(Mainnet, 0), GossipTopics.DataColumnSidecarTopicName(Subnet)), Snappy.CompressToArray(DataColumnSidecar.Encode(Sidecar()))));

        Assert.That((validity, gossip.GetDropCount(GossipDropReason.UnknownTopic), raised), Is.EqualTo((MessageValidity.Ignored, 1L, 0)));
    }

    [TestCase(true, MessageValidity.Accepted, TestName = "Gloas sidecar of a held block is accepted and stored")]
    [TestCase(false, MessageValidity.Ignored, TestName = "Gloas sidecar of an unknown block is parked")]
    public void Gloas_digest_column_is_validated_as_the_gloas_sidecar(bool blockHeld, MessageValidity expected)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot + 1);
        block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        if (blockHeld)
        {
            store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
        }

        SlotClock clock = new(Sepolia, new ManualTimestamper(SlotStart(Sepolia, FirstGloasSlot + 1).AddSeconds(6)));
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter columns = new(Sepolia, clock, LimboLogs.Instance, pool, store);
        byte[] digest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch);
        columns.Start(_ => new SilentTopic(), digest, [Subnet]);
        GossipMessageValidator validator = new(new GossipRouter(Sepolia, clock, LimboLogs.Instance), columns, Sepolia, clock);
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Subnet, FirstGloasSlot + 1, root);

        MessageValidity validity = validator.Verify(Message(GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(Subnet)), Snappy.CompressToArray(DataColumnSidecarGloas.Encode(sidecar))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected));
            Assert.That(pool.TryGetGloas(root, Subnet, out _), Is.EqualTo(blockHeld), "stored as verified");
            Assert.That(pool.GetPendingGloas(root, Subnet), Has.Length.EqualTo(blockHeld ? 0 : 1), "parked as a pending candidate");
        }
    }

    private static (GossipMessageValidator Validator, GossipRouter Gossip, ColumnGossipRouter Columns, DataColumnSidecarPool Pool) CreateMainnet(ulong finalizedEpoch = 0)
    {
        ManualTimestamper timestamper = new(SlotStart(Mainnet, MainnetSlot).AddSeconds(6));
        SlotClock clock = new(Mainnet, timestamper);
        BeaconChainStatusHolder status = new(Mainnet, timestamper)
        {
            CurrentStatus = new StatusMessageV2 { ForkDigest = MainnetDigest, FinalizedRoot = Hash256.Zero, FinalizedEpoch = finalizedEpoch, HeadRoot = Hash256.Zero },
        };
        DataColumnSidecarPool pool = new();
        ColumnGossipRouter columns = new(Mainnet, clock, LimboLogs.Instance, pool, status: status);
        columns.Start(_ => new SilentTopic(), MainnetDigest, [Subnet]);
        GossipRouter gossip = new(Mainnet, clock, LimboLogs.Instance, status: status);
        return (new GossipMessageValidator(gossip, columns, Mainnet, clock), gossip, columns, pool);
    }

    private static DataColumnSidecar Sidecar(Action<DataColumnSidecar>? mutate = null)
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Subnet, MainnetSlot);
        mutate?.Invoke(sidecar);
        return sidecar;
    }

    private static void Widen(DataColumnSidecar sidecar, int blobs)
    {
        sidecar.Column = [.. Enumerable.Repeat(sidecar.Column![0], blobs)];
        sidecar.KzgCommitments = [.. Enumerable.Repeat(sidecar.KzgCommitments![0], blobs)];
        sidecar.KzgProofs = [.. Enumerable.Repeat(sidecar.KzgProofs![0], blobs)];
    }

    private static DateTime SlotStart(BeaconChainSpec spec, ulong slot) => DateTime.UnixEpoch.AddSeconds(spec.GenesisTime + slot * spec.SecondsPerSlot);

    private static Message Message(string topic, byte[] data) => new() { Topic = topic, Data = ByteString.CopyFrom(data), Signature = ByteString.Empty };

    private static TestCaseData Fulu(string name, DataColumnSidecar sidecar, ulong finalizedEpoch, MessageValidity expected, bool consumed, ColumnGossipDropReason? reason) =>
        new TestCaseData(sidecar, finalizedEpoch, expected, consumed, reason).SetName(name);

    private sealed class SilentTopic : ITopic
    {
        public event Action<byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed => true;

        public void Subscribe() { }

        public void Unsubscribe() { }

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }
    }
}
