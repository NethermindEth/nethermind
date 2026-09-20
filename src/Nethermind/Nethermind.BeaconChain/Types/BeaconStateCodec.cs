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
    /// Holds for the Electra and Fulu layouts, which is all this reader needs: a Gloas state's
    /// progressive encoding is not assumed to put the slot here, and whatever is read from a
    /// non-Fulu state resolves to a fork that is then refused.
    /// </remarks>
    private const int SlotOffset = 40;

    /// <summary>Decodes <paramref name="ssz"/> as the only state layout this driver processes.</summary>
    /// <exception cref="BeaconStateException">The state is too short to carry a slot.</exception>
    /// <exception cref="NotSupportedException">The state belongs to a fork this driver cannot process.</exception>
    public static BeaconStateFulu Decode(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        if (ssz.Length < SlotOffset + sizeof(ulong))
        {
            throw new BeaconStateException($"Beacon state SSZ is {ssz.Length} bytes, too short to contain a slot");
        }

        ulong slot = BinaryPrimitives.ReadUInt64LittleEndian(ssz[SlotOffset..]);
        BeaconFork fork = spec.ForkAtEpoch(spec.GetEpoch(slot));
        if (fork != BeaconFork.Fulu)
        {
            throw new NotSupportedException(
                $"Beacon state at slot {slot} belongs to the {fork} fork; this driver can only process Fulu states");
        }

        BeaconStateFulu.Decode(ssz, out BeaconStateFulu state);
        return state;
    }
}
