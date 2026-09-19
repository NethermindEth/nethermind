// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    [TestCaseSource(nameof(CodeChanges))]
    public void ReportCodeChange_WhenCodeChanges_ReportsExpectedWipe(byte[]? before, byte[] after, bool expected, string reason) =>
        Assert.That(ReportsWipe(before, after), Is.EqualTo(expected), reason);

    private static TestCaseData[] CodeChanges =>
    [
        new TestCaseData(Contract, OtherContract, true, "recreation replaces existing code").SetName("ContractCodeReplacingContractCode_IsAWipe"),
        new TestCaseData(Array.Empty<byte>(), Contract, true, "creation clears existing storage").SetName("ContractCodeOnAnAccountThatExistedWithoutCode_IsAWipe"),
        new TestCaseData(Contract, Array.Empty<byte>(), true, "empty runtime recreation still clears storage").SetName("EmptyCodeReplacingContractCode_IsAWipe"),
        new TestCaseData(null, Contract, false, "a fresh account has no storage to clear").SetName("ContractCodeOnAFreshAccount_IsNotAWipe"),
        new TestCaseData(Array.Empty<byte>(), Delegation, false, "delegation keeps storage").SetName("SettingADelegation_IsNotAWipe"),
        new TestCaseData(Delegation, Array.Empty<byte>(), false, "revocation keeps storage").SetName("RevokingADelegation_IsNotAWipe"),
        new TestCaseData(Delegation, (byte[])[.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes], false, "replacement delegation keeps storage").SetName("ReplacingADelegation_IsNotAWipe")
    ];

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
