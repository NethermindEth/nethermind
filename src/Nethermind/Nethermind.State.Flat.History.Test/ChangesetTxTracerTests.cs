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
    public void ReportCodeChange_DoesNotInferAWipe(byte[]? before, byte[] after) =>
        Assert.That(WipedBy(tracer => tracer.ReportCodeChange(TestItem.AddressA, before, after)), Is.False,
            "a wipe is recorded where it is observed, not inferred from code appearing over an account");

    private static TestCaseData[] CodeChanges =>
    [
        new TestCaseData(Contract, OtherContract).SetName("ContractCodeReplacingContractCode"),
        new TestCaseData(Array.Empty<byte>(), Contract).SetName("ContractCodeOnAnAccountThatExistedWithoutCode"),
        new TestCaseData(Contract, Array.Empty<byte>()).SetName("EmptyCodeReplacingContractCode"),
        new TestCaseData(null, Contract).SetName("ContractCodeOnAFreshAccount"),
        new TestCaseData(Array.Empty<byte>(), Delegation).SetName("SettingADelegation"),
        new TestCaseData(Delegation, Array.Empty<byte>()).SetName("RevokingADelegation"),
        new TestCaseData(Delegation, (byte[])[.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes]).SetName("ReplacingADelegation")
    ];

    [Test]
    public void ReportStorageClear_IsWhatRecordsAWipe() =>
        Assert.That(WipedBy(tracer => tracer.ReportStorageClear(TestItem.AddressA)), Is.True,
            "every path that clears storage inside a transaction journals the clear and reports it here");

    [Test]
    public void ReportCodeChange_WithoutCode_RecordsTheAccountAsDeleted()
    {
        ChangesetCollector collector = new();
        new ChangesetTxTracer(collector).ReportCodeChange(TestItem.AddressA, Contract, null);
        MidBlockOverlay overlay = Fold(collector);

        overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);

        Assert.That(account!.Emptied && !account.Exists, Is.True);
    }

    private static bool WipedBy(Action<ChangesetTxTracer> report)
    {
        ChangesetCollector collector = new();
        report(new ChangesetTxTracer(collector));
        MidBlockOverlay overlay = Fold(collector);

        return overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account)
            && account.StorageClearedAt != MidBlockOverlay.NeverCleared;
    }

    private static MidBlockOverlay Fold(ChangesetCollector collector)
    {
        MidBlockOverlay overlay = new();
        overlay.Reset(1);
        overlay.Fold(0, collector.Pack());
        collector.Release();
        return overlay;
    }
}
