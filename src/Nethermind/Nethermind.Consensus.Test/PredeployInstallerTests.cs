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
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PredeployInstallerTests
{
    public enum NonEmptyAccount
    {
        CanonicalCode,
        ForeignCode,
        Storage,
    }

    [Test]
    public void Recent_root_predeploy_over_a_non_empty_account_is_rejected_at_activation_and_left_alone_after_it(
        [Values] NonEmptyAccount account, [Values] bool activeInParent)
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8272Enabled = true };
        Address predeploy = Eip8272Constants.RecentRootAddress;
        StorageCell cell = new(predeploy, 1);
        UInt256 storedValue = account == NonEmptyAccount.Storage ? UInt256.One : UInt256.Zero;
        byte[] code = account switch
        {
            NonEmptyAccount.CanonicalCode => Eip8272Constants.RecentRootCode.ToArray(),
            NonEmptyAccount.ForeignCode => [0x5f, 0x5f, 0xfd],
            _ => [],
        };

        IWorldState state = TestWorldStateFactory.CreateForTest();
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(predeploy, 0);
            if (code.Length != 0)
            {
                state.InsertCode(predeploy, code, spec);
            }

            state.Set(cell, storedValue);
            state.Commit(spec, isGenesis: true);
            state.CommitTree(0);

            bool installed = PredeployInstaller.Install(state, state, spec, activeInParent ? spec : Amsterdam.Instance);
            state.Get(cell, out UInt256 value);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(installed, Is.EqualTo(activeInParent));
                Assert.That(state.GetCode(predeploy).ToArray(), Is.EqualTo(code));
                Assert.That(state.GetNonce(predeploy), Is.Zero);
                Assert.That(value, Is.EqualTo(storedValue));
            }
        }
    }

    [TestCase(0ul, 1ul, false)]
    [TestCase(5ul, 5ul, false)]
    [TestCase(0ul, 1ul, true)]
    public void Recent_root_predeploy_over_a_codeless_account_keeps_its_balance_and_raises_its_nonce_to_one(ulong existingNonce, ulong expectedNonce, bool activeInParent)
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8272Enabled = true };
        Address predeploy = Eip8272Constants.RecentRootAddress;
        UInt256 balance = 7;

        IWorldState state = TestWorldStateFactory.CreateForTest();
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(predeploy, balance, existingNonce);
            state.Commit(spec, isGenesis: true);

            bool installed = PredeployInstaller.Install(state, state, spec, activeInParent ? spec : Amsterdam.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(installed, Is.True);
                Assert.That(state.GetCode(predeploy).ToArray(), Is.EqualTo(Eip8272Constants.RecentRootCode.ToArray()));
                Assert.That(state.GetNonce(predeploy), Is.EqualTo(expectedNonce));
                Assert.That(state.GetBalance(predeploy), Is.EqualTo(balance));
            }
        }
    }

    [TestCase(0UL, 1UL)]
    [TestCase(5UL, 5UL)]
    public void Nonce_manager_predeploy_installs_its_code_at_the_higher_of_its_nonce_and_one(ulong existingNonce, ulong expectedNonce)
    {
        (IReleaseSpec spec, _, IWorldState writeState) =
            Install(static spec => spec.IsEip8250Enabled.Returns(true), Eip8250Constants.NonceManagerAddress, nonce: existingNonce, code: []);

        writeState.Received().InsertCode(Eip8250Constants.NonceManagerAddress, Eip8250Constants.NonceManagerCode, spec);
        writeState.Received().SetNonce(Eip8250Constants.NonceManagerAddress, expectedNonce);
        writeState.DidNotReceive().SetNonce(Eip8250Constants.NonceManagerAddress, Arg.Is<ulong>(n => n != expectedNonce));
    }

    [Test]
    public void Eip8141_activation_does_not_write_the_expiry_verifier([Values] bool noncanonicalPrestate)
    {
        (_, _, IWorldState writeState) = Install(
            static spec => spec.IsEip8141Enabled.Returns(true),
            Eip8141Constants.ExpiryVerifierAddress,
            nonce: noncanonicalPrestate ? 7UL : 0UL,
            code: noncanonicalPrestate ? [0x00] : []);

        Assert.That(writeState.ReceivedCalls(), Is.Empty);
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

            PredeployInstaller.Install(inner, traced, spec, spec);

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
        PredeployInstaller.Install(readState, writeState, spec, spec);

        return (spec, readState, writeState);
    }
}
