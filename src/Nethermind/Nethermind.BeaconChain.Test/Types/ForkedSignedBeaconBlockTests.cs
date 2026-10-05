// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Types;

public class ForkedSignedBeaconBlockTests
{
    [Test]
    public void Accessors_read_the_message_of_either_shape([Values] bool gloas)
    {
        const ulong ProposerIndex = 7;
        Hash256 parentRoot = TestItem.KeccakA;
        ForkedSignedBeaconBlock block;
        Hash256 messageRoot;
        Hash256 signedRoot;
        if (gloas)
        {
            SignedBeaconBlockGloas signed = CreateMinimalGloasBlock(FirstGloasSlot, parentRoot);
            signed.Message!.ProposerIndex = ProposerIndex;
            block = new ForkedSignedBeaconBlock.OfGloas(signed);
            messageRoot = SszRoots.HashTreeRoot(signed.Message);
            signedRoot = SszRoots.HashTreeRoot(signed);
        }
        else
        {
            SignedBeaconBlock signed = CreateMinimalBlock(FirstGloasSlot - 1);
            signed.Message!.ParentRoot = parentRoot;
            signed.Message.ProposerIndex = ProposerIndex;
            block = new ForkedSignedBeaconBlock.OfFulu(signed);
            messageRoot = SszRoots.HashTreeRoot(signed.Message);
            signedRoot = SszRoots.HashTreeRoot(signed);
        }

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(block.ParentRoot, Is.EqualTo(parentRoot), "parent root");
        Assert.That(block.ProposerIndex, Is.EqualTo(ProposerIndex), "proposer index");
        Assert.That(block.ComputeMessageRoot(), Is.EqualTo(messageRoot), "the block root is the message root");
        Assert.That(block.ComputeMessageRoot(), Is.Not.EqualTo(signedRoot), "the block root is not the signed container's root");
    }
}
