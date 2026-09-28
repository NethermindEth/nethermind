// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class ColumnGossipRouterCompressedSizeTests
{
    private const ulong SubnetId = 5;

    // phase0 p2p "Gossipsub size limits": max_compressed_len(MAX_PAYLOAD_SIZE) = 12233418; the column type bound caps only the uncompressed size.
    [TestCase(12233418, false, 0, TestName = "Fulu column message at max_compressed_len(MAX_PAYLOAD_SIZE) is not refused for its size")]
    [TestCase(12233419, false, 1, TestName = "Fulu column message one byte over max_compressed_len(MAX_PAYLOAD_SIZE)")]
    [TestCase(12233418, true, 0, TestName = "Gloas column message at max_compressed_len(MAX_PAYLOAD_SIZE) is not refused for its size")]
    [TestCase(12233419, true, 1, TestName = "Gloas column message one byte over max_compressed_len(MAX_PAYLOAD_SIZE)")]
    public void Compressed_payload_over_max_compressed_len_is_rejected_before_decompression(int compressedLength, bool gloasTopic, int oversized)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        ColumnGossipRouter router = new(spec, new SlotClock(spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(spec.GenesisTime))), LimboLogs.Instance);
        router.Start(static _ => Substitute.For<ITopic>(), ForkDigest.Compute(spec, 0), [SubnetId]);

        // Zeros declare an empty payload, so below the bound the message fails as snappy or SSZ, never as oversized.
        MessageValidity validity = router.Handle(SubnetId, gloasTopic, new byte[compressedLength]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Oversized), Is.EqualTo(oversized));
        }
    }
}
