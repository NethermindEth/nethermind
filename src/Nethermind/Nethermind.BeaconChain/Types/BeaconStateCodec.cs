// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;

namespace Nethermind.BeaconChain.Types;

/// <summary>Decodes persisted or downloaded beacon states using their slot-selected SSZ layout.</summary>
/// <remarks>Fulu and Gloas have distinct layouts; unsupported forks are refused by name.</remarks>
public static class BeaconStateCodec
{
    /// <summary>Byte offset of <c>slot</c>: <c>genesis_time</c> (8) plus <c>genesis_validators_root</c> (32).</summary>
    /// <remarks>
    /// Holds for the Electra, Fulu and Gloas layouts: EIP-7495 serializes a <c>ProgressiveContainer</c>
    /// exactly as a <c>Container</c> of its active fields, and the fields before <c>slot</c> are fixed-size.
    /// </remarks>
    private const int SlotOffset = 40;

    /// <summary>Decodes <paramref name="ssz"/> as the state layout of the fork its slot belongs to.</summary>
    /// <remarks>Callers must verify the decoded <c>fork.current_version</c>; the slot alone selects the layout.</remarks>
    /// <exception cref="BeaconStateException">The state is too short to carry a slot, or its slot predates Electra.</exception>
    /// <exception cref="NotSupportedException">The state is an Electra state, which this driver cannot process.</exception>
    /// <exception cref="System.IO.InvalidDataException">The body is malformed for the layout its slot selects.</exception>
    public static ForkedBeaconState DecodeForked(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        ulong slot = ReadSlot(ssz);
        BeaconFork fork = spec.ForkAtEpoch(spec.GetEpoch(slot));
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

    /// <summary>Reads <c>slot</c> without decoding the rest of the state.</summary>
    /// <exception cref="BeaconStateException">The state is too short to carry a slot.</exception>
    internal static ulong ReadSlot(ReadOnlySpan<byte> ssz) =>
        ssz.Length < SlotOffset + sizeof(ulong)
            ? throw new BeaconStateException($"Beacon state SSZ is {ssz.Length} bytes, too short to contain a slot")
            : BinaryPrimitives.ReadUInt64LittleEndian(ssz[SlotOffset..]);
}
