// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.Types;

[HardTimeout(60_000)]
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
    public void A_state_too_short_to_carry_a_slot_is_refused_rather_than_read_past_its_end() =>
        Assert.Throws<BeaconStateException>(
            () => BeaconStateCodec.DecodeForked(new byte[SlotOffset], BeaconChainSpec.Sepolia));

    [Test]
    public void A_supported_state_reaches_the_decoder([Values] bool gloas)
    {
        BeaconChainSpec sepolia = BeaconChainSpec.Sepolia;
        byte[] truncatedState = StateStartingAtSlot((gloas ? sepolia.GloasForkEpoch : sepolia.FuluForkEpoch) * sepolia.SlotsPerEpoch);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => BeaconStateCodec.DecodeForked(truncatedState, sepolia))!;

        Assert.That(ex.Message, Does.Contain(gloas ? nameof(BeaconStateGloas) : nameof(BeaconStateFulu)));
    }

    [Test]
    public void A_state_decodes_forked_in_the_layout_of_its_own_fork([Values] bool gloas)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        byte[] ssz = gloas ? BeaconStateGloas.Encode(chain.First.PostState) : BeaconStateFulu.Encode(chain.AnchorState);

        ForkedBeaconState decoded = BeaconStateCodec.DecodeForked(ssz, chain.Spec);

        Hash256 root = decoded switch
        {
            ForkedBeaconState.OfGloas g => SszRoots.HashTreeRoot(g.State),
            ForkedBeaconState.OfFulu f => SszRoots.HashTreeRoot(f.State),
            _ => throw new AssertionException($"Unexpected shape {decoded.GetType().Name}"),
        };
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(decoded.Fork, Is.EqualTo(gloas ? BeaconFork.Gloas : BeaconFork.Fulu));
        Assert.That(root, Is.EqualTo(gloas ? SszRoots.HashTreeRoot(chain.First.PostState) : SszRoots.HashTreeRoot(chain.AnchorState)));
    }

    [Test]
    public void An_electra_state_is_refused_by_name_by_the_forked_decoder()
    {
        BeaconChainSpec sepolia = BeaconChainSpec.Sepolia;

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => BeaconStateCodec.DecodeForked(StateStartingAtSlot(sepolia.ElectraForkEpoch * sepolia.SlotsPerEpoch), sepolia))!;

        Assert.That(ex.Message, Does.Contain(nameof(BeaconFork.Electra)));
    }
}
