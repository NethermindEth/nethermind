// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// gloas/p2p-interface.md orders verify_data_column_sidecar's REJECT after the future-slot and block-seen IGNOREs.
public class ColumnGossipRouterHeldBlockTests
{
    private const ulong Column = 5;
    private static readonly ulong BlockSlot = FirstGloasSlot + 1;

    [TestCase(0UL, MessageValidity.Rejected, TestName = "Malformed Gloas sidecar naming a held block is rejected")]
    [TestCase(1UL, MessageValidity.Ignored, TestName = "Malformed Gloas sidecar naming a held block from the next slot is only ignored")]
    public void Malformed_sidecar_is_convicted_only_by_a_held_block_and_a_slot_not_from_the_future(ulong slotsAhead, MessageValidity expected)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(BlockSlot);
        block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
        DataColumnSidecarPool pool = new();
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + BlockSlot * Sepolia.SecondsPerSlot + 6));
        ColumnGossipRouter router = new(Sepolia, new SlotClock(Sepolia, clock), LimboLogs.Instance, pool, store);
        router.Start(static _ => new NullTopic(), ForkDigest.Compute(Sepolia, Sepolia.GetEpoch(BlockSlot)), [Column]);

        ulong outOfRangeIndex = Column + Eip7594DasConstants.DataColumnSidecarSubnetCount;
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, BlockSlot + slotsAhead, root);
        sidecar.Index = outOfRangeIndex;

        MessageValidity validity = router.Handle(Column, gloasTopic: true, Snappy.CompressToArray(DataColumnSidecarGloas.Encode(sidecar)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedStructure), Is.EqualTo(1));
            Assert.That(pool.GetPendingGloas(root, outOfRangeIndex), Is.Empty, "a malformed sidecar is never parked");
        }
    }

    private sealed class NullTopic : ITopic
    {
        public event Action<byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed => true;

        public void Subscribe() { }

        public void Unsubscribe() { }

        public void Publish(byte[] value) { }

        public void Publish(Google.Protobuf.IMessage value) { }
    }
}
