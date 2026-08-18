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
        (_, IReadOnlyStateProvider readState, IWorldState writeState) =
            Install(static spec => spec.IsEip8272Enabled.Returns(true), Eip8272Constants.RecentRootAddress, nonce: 1, code: [0x60, 0x00]);

        readState.DidNotReceive().GetCode(Eip8272Constants.RecentRootAddress);
        writeState.DidNotReceive().SetNonce(Eip8272Constants.RecentRootAddress, Arg.Any<ulong>());
    }

    [Test]
    public void Expiry_verifier_predeploy_installs_its_code_without_touching_the_nonce()
    {
        (IReleaseSpec spec, _, IWorldState writeState) =
            Install(static spec => spec.IsEip8141Enabled.Returns(true), Eip8141Constants.ExpiryVerifierAddress, nonce: 0, code: []);

        writeState.Received().InsertCode(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, spec);
        writeState.DidNotReceive().SetNonce(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ulong>());
    }

    [Test]
    public void Expiry_verifier_predeploy_carrying_its_code_at_a_zero_nonce_writes_nothing()
    {
        (IReleaseSpec spec, _, IWorldState writeState) =
            Install(static spec => spec.IsEip8141Enabled.Returns(true), Eip8141Constants.ExpiryVerifierAddress, nonce: 0, code: Eip8141Constants.ExpiryVerifierCode);

        writeState.DidNotReceive().InsertCode(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ReadOnlyMemory<byte>>(), spec);
        writeState.DidNotReceive().SetNonce(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ulong>());
        // Re-creating the account each block would land back in the BAL, which is the failure this predeploy's
        // null nonce exists to avoid.
        writeState.DidNotReceiveWithAnyArgs().CreateAccountIfNotExists(default!, default, default);
    }

    private static (IReleaseSpec Spec, IReadOnlyStateProvider ReadState, IWorldState WriteState) Install(
        Action<IReleaseSpec> activate, Address predeploy, ulong nonce, byte[] code)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        activate(spec);

        IReadOnlyStateProvider readState = Substitute.For<IReadOnlyStateProvider>();
        readState.GetNonce(predeploy).Returns(nonce);
        readState.GetCode(predeploy).Returns(code);

        IWorldState writeState = Substitute.For<IWorldState>();
        PredeployInstaller.Install(readState, writeState, spec);

        return (spec, readState, writeState);
    }
}
