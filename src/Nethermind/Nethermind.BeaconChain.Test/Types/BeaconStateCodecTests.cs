// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Types;

/// <summary>
/// Electra, Fulu and Gloas are three different state layouts, and the driver decoded every
/// persisted state as Fulu whatever wrote it. Refusing by name beats decoding a Gloas state
/// against Fulu's layout, which yields wrong field values rather than an error.
/// </summary>
public class BeaconStateCodecTests
{
    private const int SlotOffset = 40;

    private static byte[] StateStartingAtSlot(ulong slot)
    {
        byte[] ssz = new byte[SlotOffset + sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(SlotOffset), slot);
        return ssz;
    }

    [Test]
    public void A_state_from_a_fork_the_driver_cannot_process_is_refused_by_name()
    {
        BeaconChainSpec sepolia = BeaconChainSpec.Sepolia;
        byte[] gloasState = StateStartingAtSlot(sepolia.GloasForkEpoch * sepolia.SlotsPerEpoch);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => BeaconStateCodec.Decode(gloasState, sepolia))!;

        Assert.That(ex.Message, Does.Contain(nameof(BeaconFork.Gloas)));
    }

    [Test]
    public void A_state_too_short_to_carry_a_slot_is_refused_rather_than_read_past_its_end() =>
        Assert.Throws<BeaconStateException>(
            () => BeaconStateCodec.Decode(new byte[SlotOffset], BeaconChainSpec.Sepolia));

    [Test]
    public void A_fulu_state_reaches_the_decoder()
    {
        BeaconChainSpec sepolia = BeaconChainSpec.Sepolia;
        byte[] truncatedFuluState = StateStartingAtSlot(sepolia.FuluForkEpoch * sepolia.SlotsPerEpoch);

        // The fork gate passes, so the failure below comes from the SSZ decoder meeting a
        // deliberately truncated body - not from the gate rejecting a Fulu state.
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => BeaconStateCodec.Decode(truncatedFuluState, sepolia))!;

        Assert.That(ex.Message, Does.Contain(nameof(BeaconStateFulu)));
    }
}
