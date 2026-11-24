// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Types;

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(decoded, gloas ? Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>() : Is.TypeOf<ForkedSignedBeaconBlock.OfFulu>());
        Assert.That(decoded.Slot, Is.EqualTo(slot));
        Assert.That(SignedBeaconBlockCodec.Encode(decoded, Sepolia), Is.EqualTo(ssz));
    }

    [Test]
    public void Prefix_offsets_point_at_the_slot_and_roots_in_every_fork_shape([Values] bool gloas)
    {
        Hash256 parentRoot = new("0x1111111111111111111111111111111111111111111111111111111111111111");
        Hash256 stateRoot = new("0x2222222222222222222222222222222222222222222222222222222222222222");
        byte[] ssz;
        if (gloas)
        {
            SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot, parentRoot);
            block.Message!.StateRoot = stateRoot;
            ssz = SignedBeaconBlockGloas.Encode(block);
        }
        else
        {
            SignedBeaconBlock block = CreateMinimalBlock(FirstGloasSlot - 1);
            block.Message!.ParentRoot = parentRoot;
            block.Message.StateRoot = stateRoot;
            ssz = SignedBeaconBlock.Encode(block);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(ssz), Is.EqualTo(SignedBeaconBlockCodec.MessageOffset));
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(ssz.AsSpan(SignedBeaconBlockCodec.MessageOffset)), Is.EqualTo(gloas ? FirstGloasSlot : FirstGloasSlot - 1));
        Assert.That(new Hash256(ssz.AsSpan(SignedBeaconBlockCodec.ParentRootOffset, Hash256.Size)), Is.EqualTo(parentRoot));
        Assert.That(new Hash256(ssz.AsSpan(SignedBeaconBlockCodec.ParentRootOffset + Hash256.Size, Hash256.Size)), Is.EqualTo(stateRoot));
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
