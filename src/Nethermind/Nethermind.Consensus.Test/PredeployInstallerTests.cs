// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PredeployInstallerTests
{
    [Test]
    public void Empty_canonical_predeploy_at_its_nonce_reads_no_code_and_writes_nothing()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8272Enabled.Returns(true);

        IReadOnlyStateProvider readState = Substitute.For<IReadOnlyStateProvider>();
        readState.GetNonce(Eip8272Constants.RecentRootAddress).Returns(1ul);
        readState.GetCode(Eip8272Constants.RecentRootAddress).Returns([0x60, 0x00]);

        IWorldState writeState = Substitute.For<IWorldState>();

        PredeployInstaller.Install(readState, writeState, spec);

        readState.DidNotReceive().GetCode(Eip8272Constants.RecentRootAddress);
        writeState.DidNotReceive().SetNonce(Eip8272Constants.RecentRootAddress, Arg.Any<ulong>());
    }

    [Test]
    public void Expiry_verifier_predeploy_installs_its_code_without_touching_the_nonce()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);

        IReadOnlyStateProvider readState = Substitute.For<IReadOnlyStateProvider>();
        readState.GetNonce(Eip8141Constants.ExpiryVerifierAddress).Returns(0ul);
        readState.GetCode(Eip8141Constants.ExpiryVerifierAddress).Returns([]);

        IWorldState writeState = Substitute.For<IWorldState>();

        PredeployInstaller.Install(readState, writeState, spec);

        writeState.Received().InsertCode(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, spec);
        writeState.DidNotReceive().SetNonce(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ulong>());
    }

    [Test]
    public void Expiry_verifier_predeploy_carrying_its_code_at_a_zero_nonce_writes_nothing()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);

        IReadOnlyStateProvider readState = Substitute.For<IReadOnlyStateProvider>();
        readState.GetNonce(Eip8141Constants.ExpiryVerifierAddress).Returns(0ul);
        readState.GetCode(Eip8141Constants.ExpiryVerifierAddress).Returns(Eip8141Constants.ExpiryVerifierCode);

        IWorldState writeState = Substitute.For<IWorldState>();

        PredeployInstaller.Install(readState, writeState, spec);

        writeState.DidNotReceive().InsertCode(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ReadOnlyMemory<byte>>(), spec);
        writeState.DidNotReceive().SetNonce(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ulong>());
    }
}
