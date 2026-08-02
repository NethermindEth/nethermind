// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class PredeployInstallerTests
{
    private static readonly IReleaseSpec FrameFamilySpec = new OverridableReleaseSpec(Cancun.Instance)
    {
        IsEip8141Enabled = true,
        IsEip8250Enabled = true,
        IsEip8272Enabled = true,
    };

    // A codeless predeploy is a storage namespace, so only its nonce distinguishes it from an absent
    // account. Leaving it at zero forks the state root against a client applying the convention.
    [TestCase(nameof(Eip8141Constants.ExpiryVerifierAddress))]
    [TestCase(nameof(Eip8250Constants.NonceManagerAddress))]
    [TestCase(nameof(Eip8272Constants.RecentRootAddress))]
    public void Install_EveryActivatedPredeploy_CarriesThePredeployNonce(string predeploy)
    {
        Address address = predeploy switch
        {
            nameof(Eip8141Constants.ExpiryVerifierAddress) => Eip8141Constants.ExpiryVerifierAddress,
            nameof(Eip8250Constants.NonceManagerAddress) => Eip8250Constants.NonceManagerAddress,
            _ => Eip8272Constants.RecentRootAddress,
        };

        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        PredeployInstaller.Install(state, state, FrameFamilySpec);

        Assert.That(state.GetNonce(address), Is.EqualTo(1UL));
    }

    [Test]
    public void Install_RunTwice_IsIdempotent()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        PredeployInstaller.Install(state, state, FrameFamilySpec);
        state.Commit(FrameFamilySpec);
        state.CommitTree(0);
        Hash256 afterFirst = state.StateRoot;

        PredeployInstaller.Install(state, state, FrameFamilySpec);
        state.Commit(FrameFamilySpec);
        state.CommitTree(1);

        Assert.That(state.StateRoot, Is.EqualTo(afterFirst));
    }
}
