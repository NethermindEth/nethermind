// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;

namespace Nethermind.BeaconChain.Types;

/// <summary>
/// Decodes a persisted or downloaded beacon state after establishing which fork wrote it.
/// </summary>
/// <remarks>
/// Electra, Fulu and Gloas are three different SSZ layouts, and Gloas is a progressive container
/// rather than an extension of Fulu. Decoding whatever arrives as <see cref="BeaconStateFulu"/>
/// either throws somewhere unhelpful or silently yields wrong field values, so the fork is resolved
/// first and anything this driver cannot represent is refused by name.
/// </remarks>
public static class BeaconStateCodec
{
    /// <summary>Byte offset of <c>slot</c>: <c>genesis_time</c> (8) plus <c>genesis_validators_root</c> (32).</summary>
    /// <remarks>
    /// Holds for the Electra, Fulu and Gloas layouts: EIP-7495 serializes a <c>ProgressiveContainer</c>
    /// exactly as a <c>Container</c> of its active fields, and the fields before <c>slot</c> are fixed-size.
    /// </remarks>
    private const int SlotOffset = 40;

    /// <summary>Decodes <paramref name="ssz"/> as the only state layout this driver processes.</summary>
    /// <exception cref="BeaconStateException">The state is too short to carry a slot.</exception>
    /// <exception cref="NotSupportedException">The state belongs to a fork this driver cannot process.</exception>
    public static BeaconStateFulu Decode(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        BeaconFork fork = ForkOf(ssz, spec, out ulong slot);
        if (fork != BeaconFork.Fulu)
        {
            throw new NotSupportedException(
                $"Beacon state at slot {slot} belongs to the {fork} fork; this driver can only process Fulu states");
        }

        BeaconStateFulu.Decode(ssz, out BeaconStateFulu state);
        return state;
    }

    /// <summary>Decodes <paramref name="ssz"/> as the state layout of the fork its slot belongs to.</summary>
    /// <remarks>
    /// Only the slot selects the layout; a caller holding untrusted bytes must still check that the
    /// decoded <c>fork.current_version</c> agrees. As in <see cref="SignedBeaconBlockCodec"/>, a malformed
    /// body surfaces the SSZ decoder's <see cref="System.IO.InvalidDataException"/>.
    /// </remarks>
    /// <exception cref="BeaconStateException">The state is too short to carry a slot, or its slot predates Electra.</exception>
    /// <exception cref="NotSupportedException">The state is an Electra state, which this driver cannot process.</exception>
    /// <exception cref="System.IO.InvalidDataException">The body is malformed for the layout its slot selects.</exception>
    public static ForkedBeaconState DecodeForked(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        BeaconFork fork = ForkOf(ssz, spec, out ulong slot);
        switch (fork)
        {
            case BeaconFork.Fulu:
                BeaconStateFulu.Decode(ssz, out BeaconStateFulu fulu);
                return new ForkedBeaconState.OfFulu(fulu);
            case BeaconFork.Gloas:
                BeaconStateGloas.Decode(ssz, out BeaconStateGloas gloas);
                return new ForkedBeaconState.OfGloas(gloas);
            default:
                throw new NotSupportedException(
                    $"Beacon state at slot {slot} belongs to the {fork} fork; this driver can only process Fulu and Gloas states");
        }
    }

    private static BeaconFork ForkOf(ReadOnlySpan<byte> ssz, BeaconChainSpec spec, out ulong slot)
    {
        if (ssz.Length < SlotOffset + sizeof(ulong))
        {
            throw new BeaconStateException($"Beacon state SSZ is {ssz.Length} bytes, too short to contain a slot");
        }

        slot = BinaryPrimitives.ReadUInt64LittleEndian(ssz[SlotOffset..]);
        return spec.ForkAtEpoch(spec.GetEpoch(slot));
    }
}
