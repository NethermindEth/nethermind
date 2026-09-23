// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Types;

/// <summary>
/// Fulu and Gloas blocks are unrelated SSZ shapes and the bytes name no fork, so the codec reads the
/// slot first. Decoding a Gloas block as Fulu either throws or yields wrong field values, which drops
/// every block of the new fork.
/// </summary>
public class SignedBeaconBlockCodecTests
{
    private const int MessageOffset = 100;

    private static byte[] EncodeShapeOfFork(ulong slot, bool gloas) => gloas
        ? SignedBeaconBlockGloas.Encode(CreateMinimalGloasBlock(slot))
        : SignedBeaconBlock.Encode(CreateMinimalBlock(slot));

    // Slot 0 on Sepolia predates Electra and keeps the pre-Gloas shape instead of being refused.
    private static readonly TestCaseData[] SlotCases =
    [
        new TestCaseData(0ul, false).SetArgDisplayNames("before Electra"),
        new TestCaseData(FirstGloasSlot - 1, false).SetArgDisplayNames("last Fulu slot"),
        new TestCaseData(FirstGloasSlot, true).SetArgDisplayNames("first Gloas slot"),
    ];

    [TestCaseSource(nameof(SlotCases))]
    public void Each_slot_decodes_to_the_shape_of_its_fork(ulong slot, bool gloas)
    {
        byte[] ssz = EncodeShapeOfFork(slot, gloas);

        ForkedSignedBeaconBlock decoded = SignedBeaconBlockCodec.Decode(ssz, Sepolia);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, gloas ? Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>() : Is.TypeOf<ForkedSignedBeaconBlock.OfFulu>());
            Assert.That(decoded.Slot, Is.EqualTo(slot));
            Assert.That(SignedBeaconBlockCodec.Encode(decoded, Sepolia), Is.EqualTo(ssz));
        }
    }

    [Test]
    public void A_message_offset_other_than_the_fixed_part_length_is_refused([Values(0u, 96u, 104u)] uint offset)
    {
        byte[] ssz = EncodeShapeOfFork(FirstGloasSlot, gloas: true);
        BinaryPrimitives.WriteUInt32LittleEndian(ssz, offset);

        Assert.That(() => SignedBeaconBlockCodec.Decode(ssz, Sepolia), Throws.TypeOf<BeaconStateException>().With.Message.Contains("offset"));
    }

    [Test]
    public void A_block_too_short_to_carry_a_slot_is_refused_rather_than_read_past_its_end([Values(0, 3, MessageOffset + sizeof(ulong) - 1)] int length)
    {
        byte[] ssz = new byte[length];
        if (length >= sizeof(uint)) BinaryPrimitives.WriteUInt32LittleEndian(ssz, MessageOffset);

        Assert.That(() => SignedBeaconBlockCodec.Decode(ssz, Sepolia), Throws.TypeOf<BeaconStateException>());
    }

    [Test]
    public void A_block_truncated_inside_its_body_is_refused([Values] bool gloas)
    {
        byte[] ssz = EncodeShapeOfFork(gloas ? FirstGloasSlot : FirstGloasSlot - 1, gloas);

        Assert.That(() => SignedBeaconBlockCodec.Decode(ssz.AsSpan(0, ssz.Length - 1), Sepolia), Throws.TypeOf<InvalidDataException>());
    }

    // Encoding a shape the slot does not select would store bytes that read back as the other fork.
    [Test]
    public void Encoding_a_shape_that_does_not_match_the_fork_of_its_slot_is_refused([Values] bool gloasShape)
    {
        ForkedSignedBeaconBlock block = gloasShape
            ? new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(FirstGloasSlot - 1))
            : new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(FirstGloasSlot));

        Assert.That(() => SignedBeaconBlockCodec.Encode(block, Sepolia), Throws.TypeOf<BeaconStateException>());
    }
}
