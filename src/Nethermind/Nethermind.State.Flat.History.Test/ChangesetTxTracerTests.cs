// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetTxTracerTests
{
    private static readonly byte[] Contract = [0x60, 0x00, 0x55];
    private static readonly byte[] OtherContract = [0x60, 0x01, 0x55];
    private static readonly byte[] Delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];

    [Test]
    public void ContractCodeReplacingContractCode_IsAWipe() =>
        Assert.That(ReportsWipe(Contract, OtherContract), Is.True, "only a destroy and re-create inside one transaction can put new code where code already was");

    [Test]
    public void ContractCodeOnAnAccountThatExistedWithoutCode_IsAWipe() =>
        Assert.That(ReportsWipe([], Contract), Is.True, "a create over an existing account wipes whatever storage it held");

    [Test]
    public void EmptyCodeReplacingContractCode_IsAWipe() =>
        Assert.That(ReportsWipe(Contract, []), Is.True, "a destroy and re-create whose init code returns nothing still wiped the storage");

    [Test]
    public void ContractCodeOnAFreshAccount_IsNotAWipe() =>
        Assert.That(ReportsWipe(null, Contract), Is.False);

    [Test]
    public void SettingADelegation_IsNotAWipe() =>
        Assert.That(ReportsWipe([], Delegation), Is.False, "a delegated account keeps its storage");

    [Test]
    public void RevokingADelegation_IsNotAWipe() =>
        Assert.That(ReportsWipe(Delegation, []), Is.False, "revoking a delegation writes empty code and leaves the authority's storage alone");

    [Test]
    public void ReplacingADelegation_IsNotAWipe() =>
        Assert.That(ReportsWipe(Delegation, [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes]), Is.False);

    private static bool ReportsWipe(byte[]? before, byte[] after)
    {
        ChangesetCollector collector = new();
        new ChangesetTxTracer(collector).ReportCodeChange(TestItem.AddressA, before, after);
        MidBlockOverlay overlay = new();
        overlay.Reset(1);
        overlay.Fold(0, collector.Pack());
        collector.Release();

        return overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account) && account.StorageClearedAt != MidBlockOverlay.NeverCleared;
    }
}
