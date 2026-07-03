// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class Eip8253TransitionTests
{
    private static readonly ReleaseSpec EnabledSpec = new() { IsEip8253Enabled = true };
    private static readonly ReleaseSpec DisabledSpec = new();

    [Test]
    public void Bumps_only_existing_zero_nonce_accounts_and_is_one_shot()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        stateProvider.CreateAccount(Eip8253Data.Accounts[0], UInt256.One);
        stateProvider.CreateAccount(Eip8253Data.Accounts[1], UInt256.One);
        stateProvider.CreateAccount(Eip8253Data.Accounts[2], UInt256.One);
        stateProvider.SetNonce(Eip8253Data.Accounts[2], 5);
        // Eip8253Data.Accounts[3] intentionally left nonexistent.

        int bumped = Eip8253Transition.Apply(stateProvider, EnabledSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bumped, Is.EqualTo(2));
            Assert.That(stateProvider.GetNonce(Eip8253Data.Accounts[0]), Is.EqualTo(1ul));
            Assert.That(stateProvider.GetNonce(Eip8253Data.Accounts[1]), Is.EqualTo(1ul));
            Assert.That(stateProvider.GetNonce(Eip8253Data.Accounts[2]), Is.EqualTo(5ul), "non-zero nonce must not be reset");
            Assert.That(stateProvider.AccountExists(Eip8253Data.Accounts[3]), Is.False, "nonexistent accounts must not be created");
        }

        Assert.That(Eip8253Transition.Apply(stateProvider, EnabledSpec), Is.Zero, "second application must be a no-op");
    }

    [Test]
    public void Does_nothing_when_disabled()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        stateProvider.CreateAccount(Eip8253Data.Accounts[0], UInt256.One);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8253Transition.Apply(stateProvider, DisabledSpec), Is.Zero);
            Assert.That(stateProvider.GetNonce(Eip8253Data.Accounts[0]), Is.Zero);
        }
    }

    [Test]
    public void Account_list_matches_eip_asset() =>
        Assert.That(Eip8253Data.Accounts, Has.Length.EqualTo(28));
}
