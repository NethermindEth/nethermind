// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXObservationCallTests
{
    [Test]
    public void ReplayClaims_MutableDeliveries_RetainsNestedScopesAndRestoresThePrefix([Values] bool nested)
    {
        DeployDelivery([0x5f, 0x54, 0x80, 0x60, 1, 0x01, 0x5f, 0x55, 0x5f, 0x52,
            0x32, 0x60, 32, 0x52, 0x60, 64, 0x5f, 0xf3]);
        RollupXObservationClaim[] claims = nested
            ? [DeliveryClaim(0) with { RollbackSpans = [4] }, DeliveryClaim(1) with { RollbackSpans = [2] },
                DeliveryClaim(2), DeliveryClaim(1), DeliveryClaim(0)]
            : [DeliveryClaim(0) with { RollbackSpans = [2] }, DeliveryClaim(1), DeliveryClaim(0)];

        Assert.That(() => RollupXObservationReplay.ReplayClaims(_state, _calls, claims, 0, claims.Length, TestItem.AddressE, default), Throws.Nothing,
            "a delivery sees earlier writes only while their source rollback scope remains open");
        StorageCell cell = new(TestItem.AddressB, UInt256.Zero);
        _state.Get(in cell, out UInt256 value);
        Assert.That(value, Is.EqualTo(UInt256.Zero), "closing all source scopes restores the canonical storage value");
        AssertRestored();
    }

    [Test]
    public void ReplayClaims_ChangedDependentOutcome_RejectsAndRestoresAllWrites()
    {
        DeployDelivery([0x5f, 0x54, 0x80, 0x60, 1, 0x01, 0x5f, 0x55, 0x5f, 0x52,
            0x32, 0x60, 32, 0x52, 0x60, 64, 0x5f, 0xf3]);
        RollupXObservationClaim[] claims = [DeliveryClaim(0) with { RollbackSpans = [2] }, DeliveryClaim(0)];

        Assert.That(() => RollupXObservationReplay.ReplayClaims(_state, _calls, claims, 0, claims.Length, TestItem.AddressE, default),
            Throws.TypeOf<EezSettlementException>(), "the second observation must execute against the retained first write");
        StorageCell cell = new(TestItem.AddressB, UInt256.Zero);
        _state.Get(in cell, out UInt256 value);
        Assert.That(value, Is.EqualTo(UInt256.Zero), "a rejected program also restores the entire scratch group");
        AssertRestored();
    }

    [Test]
    public void ReplayClaims_InvalidRollbackSpan_RejectsBeforeDelivery([Values(0, -1, 3)] int span)
    {
        DeployDelivery([0x00]);
        RollupXObservationClaim[] claims = [DeliveryClaim(0) with { RollbackSpans = [span] }, DeliveryClaim(0)];

        Assert.That(() => RollupXObservationReplay.ReplayClaims(_state, _calls, claims, 0, claims.Length, TestItem.AddressE, default),
            Throws.TypeOf<EezSettlementException>().With.Message.Contains("prefix scope"));
        AssertRestored();
    }

    [Test]
    public void VerifyClaim_Delivery_UsesTargetRevertInsteadOfSuccessfulManagerResult([Values] bool changedOutcome)
    {
        DeployDelivery([0x60, 0x45, 0x5f, 0x52, 0x60, 32, 0x5f, 0xfd]);
        RollupXObservationClaim claim = DeliveryClaim(0) with { Expected = new(changedOutcome, Word(0x45)) };

        if (changedOutcome)
            Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.TypeOf<EezSettlementException>());
        else
            Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing,
                "the manager completes its delivery although the matched target frame returns REVERT bytes");
        AssertRestored();
    }

    private void DeployDelivery(byte[] target)
    {
        Deploy(target);
        byte[] delivery = ForwardDelivery(TestItem.AddressA);
        byte[] view = [0x5b, 0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3];
        DeployManager([0x36, 0x60, 68, 0x14, 0x60, checked((byte)(7 + delivery.Length)), 0x57, .. delivery, .. view]);
        _state.InsertCode(TestItem.AddressA, ForwardDelivery(TestItem.AddressB), Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
    }

    private static byte[] ForwardDelivery(Address target) =>
        [0x36, 0x5f, 0x5f, 0x37, 0x60, 64, 0x5f, 0x36, 0x5f, 0x5f, 0x73, .. target.Bytes,
            0x5a, 0xf1, 0x50, 0x60, 64, 0x5f, 0xf3];

    private static RollupXObservationClaim DeliveryClaim(byte previous)
    {
        byte[] origin = new byte[32];
        EezConstants.SystemAddress.Bytes.CopyTo(origin.AsSpan(12));
        return new(Keccak.Zero, TestItem.AddressC, 0, TestItem.AddressB, [0xaa], UInt256.Zero,
            ObservationMode.Lookup, new(true, [.. Word(previous), .. origin]))
        { Delivery = new([0xaa], UInt256.Zero) };
    }
}
