// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The slot-to-root index inside each pool must stay bounded: slot numbers only ever increase over a
/// node's uptime, so an unbounded index would grow for as long as the process runs regardless of the
/// value cache's own LRU capacity.
/// </summary>
public class PoolSlotIndexBoundsTests
{
    [Test]
    public void DataColumnSidecarPool_slot_index_does_not_grow_past_capacity()
    {
        const int capacity = 4;
        DataColumnSidecarPool pool = new(capacity);

        for (ulong slot = 0; slot < capacity + 20; slot++)
        {
            Hash256 root = RootForSlot(slot);
            DataColumnSidecar sidecar = new()
            {
                Index = 0,
                Column = [],
                KzgCommitments = [],
                KzgProofs = [],
                SignedBlockHeader = new SignedBeaconBlockHeader { Message = new BeaconBlockHeader { Slot = slot } },
                KzgCommitmentsInclusionProof = [],
            };
            pool.Add(root, slot, sidecar);
        }

        Assert.That(pool.SlotIndexCount, Is.LessThanOrEqualTo(capacity), "the slot index must be evicted, not grow forever with every distinct slot ever seen");
    }

    [Test]
    public void ExecutionPayloadEnvelopePool_slot_index_does_not_grow_past_capacity()
    {
        const int capacity = 4;
        ExecutionPayloadEnvelopePool pool = new(capacity);

        for (ulong slot = 0; slot < capacity + 20; slot++)
        {
            Hash256 root = RootForSlot(slot);
            SignedExecutionPayloadEnvelope envelope = new()
            {
                Message = new ExecutionPayloadEnvelope
                {
                    Payload = new ExecutionPayloadGloas { SlotNumber = slot },
                    ExecutionRequests = new ExecutionRequestsGloas(),
                    BuilderIndex = 0,
                    BeaconBlockRoot = root,
                    ParentBeaconBlockRoot = Hash256.Zero,
                },
                Signature = new BlsSignature(new byte[BlsSignature.Length]),
            };
            pool.Add(root, slot, envelope);
        }

        Assert.That(pool.SlotIndexCount, Is.LessThanOrEqualTo(capacity), "the slot index must be evicted, not grow forever with every distinct slot ever seen");
    }

    private static Hash256 RootForSlot(ulong slot)
    {
        byte[] bytes = new byte[32];
        System.BitConverter.TryWriteBytes(bytes, slot);
        return new Hash256(bytes);
    }
}
