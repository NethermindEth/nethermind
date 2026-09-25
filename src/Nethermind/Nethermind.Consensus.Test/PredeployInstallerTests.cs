// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
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

        writeState.DidNotReceiveWithAnyArgs().InsertCode(default!, default, default!);
        writeState.DidNotReceive().SetNonce(Eip8141Constants.ExpiryVerifierAddress, Arg.Any<ulong>());
        // Re-creating the account each block would land back in the BAL, which is the failure this predeploy's
        // null nonce exists to avoid.
        writeState.DidNotReceiveWithAnyArgs().CreateAccountIfNotExists(default!, default, default);
    }

    /// <remarks>The install is the only in-tree writer of a no-op nonce, so it is the only way EIP-7928's
    /// "record a nonce change only when the nonce changes" rule can be observed from block processing.</remarks>
    [Test]
    public void Predeploy_already_at_its_nonce_but_missing_its_code_records_only_the_code_change()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8250Enabled = true };
        Address predeploy = Eip8250Constants.NonceManagerAddress;

        IWorldState inner = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(predeploy, 0, nonce: 1);
            inner.Commit(spec, isGenesis: true);
            inner.CommitTree(0);
            stateRoot = inner.StateRoot;
        }

        TracedAccessWorldState traced = new(inner, parallel: false);
        traced.SetGeneratingBlockAccessList(new());
        using (traced.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject))
        {
            traced.SetIndex(0);

            PredeployInstaller.Install(inner, traced, spec);

            AccountChangesAtIndex changes = traced.GetGeneratingBlockAccessList().GetAccountChanges(predeploy);
            Assert.That(changes, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(changes.CodeChange, Is.Not.Null);
                Assert.That(changes.NonceChange, Is.Null, "re-writing the nonce it already holds is not a state transition");
            }
        }
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
