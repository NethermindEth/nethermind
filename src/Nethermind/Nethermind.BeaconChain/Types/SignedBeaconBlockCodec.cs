// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;

namespace Nethermind.BeaconChain.Types;

/// <summary>
/// Encodes and decodes a signed beacon block as the SSZ shape of the fork its slot belongs to.
/// </summary>
/// <remarks>
/// Fulu's <see cref="SignedBeaconBlock"/> and Gloas's <see cref="SignedBeaconBlockGloas"/> carry
/// unrelated bodies, and the bytes name no fork, so the slot is read before the body is decoded. In
/// both shapes the fixed part of the outer container is the 4-byte <c>message</c> offset followed by
/// the 96-byte <c>signature</c>, and <c>slot</c> is the first field of <c>message</c>. Electra blocks
/// share Fulu's shape and decode to <see cref="ForkedSignedBeaconBlock.OfFulu"/>; only
/// <see cref="BeaconChainSpec.GloasForkEpoch"/> is consulted, so a slot before Electra decodes as
/// the pre-Gloas shape rather than being refused. As in <see cref="BeaconStateCodec"/>, a truncated or
/// malformed body surfaces the SSZ decoder's <see cref="System.IO.InvalidDataException"/>, so a caller
/// handling untrusted bytes must catch it as well as <see cref="BeaconStateException"/>.
/// </remarks>
public static class SignedBeaconBlockCodec
{
    /// <summary>The only valid <c>message</c> offset: the fixed part is the offset itself plus the signature.</summary>
    private const int MessageOffset = sizeof(uint) + BlsSignature.Length;

    /// <summary>Whether a block at <paramref name="slot"/> has the Gloas shape.</summary>
    internal static bool IsGloasSlot(ulong slot, BeaconChainSpec spec) => spec.GetEpoch(slot) >= spec.GloasForkEpoch;

    /// <summary>Decodes <paramref name="ssz"/> as the block shape of the fork its slot belongs to.</summary>
    /// <exception cref="BeaconStateException">The block is too short to carry a slot, or its <c>message</c> offset is not the fixed-part length.</exception>
    /// <exception cref="System.IO.InvalidDataException">The body is malformed for the shape its slot selects.</exception>
    public static ForkedSignedBeaconBlock Decode(ReadOnlySpan<byte> ssz, BeaconChainSpec spec)
    {
        if (ssz.Length < MessageOffset + sizeof(ulong))
        {
            throw new BeaconStateException($"Signed beacon block SSZ is {ssz.Length} bytes, too short to contain a slot");
        }

        uint messageOffset = BinaryPrimitives.ReadUInt32LittleEndian(ssz);
        if (messageOffset != MessageOffset)
        {
            throw new BeaconStateException($"Signed beacon block message offset is {messageOffset}, expected {MessageOffset}");
        }

        ulong slot = BinaryPrimitives.ReadUInt64LittleEndian(ssz[MessageOffset..]);
        if (IsGloasSlot(slot, spec))
        {
            SignedBeaconBlockGloas.Decode(ssz, out SignedBeaconBlockGloas gloas);
            return new ForkedSignedBeaconBlock.OfGloas(gloas);
        }

        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock fulu);
        return new ForkedSignedBeaconBlock.OfFulu(fulu);
    }

    /// <summary>Encodes <paramref name="block"/>, refusing a shape that <see cref="Decode"/> would not give back for its slot.</summary>
    /// <exception cref="BeaconStateException">The block's shape does not match the fork its slot belongs to.</exception>
    /// <exception cref="NotSupportedException">The block is of a shape this codec does not know.</exception>
    public static byte[] Encode(ForkedSignedBeaconBlock block, BeaconChainSpec spec) => block switch
    {
        ForkedSignedBeaconBlock.OfFulu fulu when !IsGloasSlot(block.Slot, spec) => SignedBeaconBlock.Encode(fulu.Block),
        ForkedSignedBeaconBlock.OfGloas gloas when IsGloasSlot(block.Slot, spec) => SignedBeaconBlockGloas.Encode(gloas.Block),
        ForkedSignedBeaconBlock.OfFulu or ForkedSignedBeaconBlock.OfGloas => throw new BeaconStateException(
            $"Block at slot {block.Slot} was constructed as {block.GetType().Name}, which is not the shape of the fork that slot belongs to"),
        _ => throw new NotSupportedException($"Unhandled signed beacon block shape {block.GetType().Name}"),
    };
}
