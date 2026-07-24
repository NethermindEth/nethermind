// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PayloadAttributesValidateTests
{
    private static PayloadAttributes BuildAttrs(bool withSlotNumber, ulong timestamp = 1_000UL) => new()
    {
        Timestamp = timestamp,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = Address.Zero,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = withSlotNumber ? 42UL : null,
        TargetGasLimit = withSlotNumber ? 30_000_000L : null,
    };

    private static ISpecProvider MakeSpecProvider(bool isAmsterdam)
    {
        ISpecProvider sp = Substitute.For<ISpecProvider>();
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip7843Enabled.Returns(isAmsterdam);
        spec.IsEip4844Enabled.Returns(true);
        spec.WithdrawalsEnabled.Returns(true);
        sp.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        return sp;
    }

    // Each case asserts a distinct branch of PayloadAttributes.Validate against the spec.
    // mustContain/mustNotContain are checked when non-null.
    private static readonly object[] ValidateCases =
    [
        new object[] { /* isAmsterdam */ true,  /* withSlot */ false, /* fcu */ PayloadAttributesVersions.V4,
            PayloadAttributesValidationResult.InvalidPayloadAttributes, "must be provided", "expected" },
        new object[] { /* isAmsterdam */ true,  /* withSlot */ true,  /* fcu */ PayloadAttributesVersions.V4,
            PayloadAttributesValidationResult.Success, null!, null! },
        new object[] { /* isAmsterdam */ false, /* withSlot */ true,  /* fcu */ PayloadAttributesVersions.V3,
            PayloadAttributesValidationResult.InvalidPayloadAttributes, null!, null! },
        new object[] { /* isAmsterdam */ true, /* withSlot */ false, /* fcu */ PayloadAttributesVersions.V3,
            PayloadAttributesValidationResult.UnsupportedFork, null!, null! },
    ];

    [TestCaseSource(nameof(ValidateCases))]
    public void Validate_returns_expected_result(
        bool isAmsterdam, bool withSlotNumber, int fcuVersion,
        PayloadAttributesValidationResult expected, string errorMustContain, string errorMustNotContain)
    {
        ISpecProvider sp = MakeSpecProvider(isAmsterdam);
        PayloadAttributes attrs = BuildAttrs(withSlotNumber);

        PayloadAttributesValidationResult result = attrs.Validate(sp, fcuVersion, out string error);

        Assert.That(result, Is.EqualTo(expected));
        if (expected == PayloadAttributesValidationResult.Success)
        {
            Assert.That(error, Is.Null);
        }
        else
        {
            Assert.That(error, Is.Not.Null);
            if (errorMustContain is not null) Assert.That(error, Does.Contain(errorMustContain));
            if (errorMustNotContain is not null) Assert.That(error, Does.Not.Contain(errorMustNotContain));
        }
    }

    [Test]
    public void Validate_accepts_null_inclusion_list_at_V5()
    {
        // EIP-7805: the proposer's initial FCUv5 has no inclusion list yet (it's an optional V5 field),
        // so a Bogota attrs shaped like V4 must validate rather than be rejected.
        ISpecProvider sp = Substitute.For<ISpecProvider>();
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip7805Enabled.Returns(true);
        spec.IsEip7843Enabled.Returns(true);
        spec.IsEip4844Enabled.Returns(true);
        spec.WithdrawalsEnabled.Returns(true);
        sp.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        PayloadAttributes attrs = BuildAttrs(withSlotNumber: true); // no InclusionListTransactions set

        PayloadAttributesValidationResult result = attrs.Validate(sp, PayloadAttributesVersions.V5, out string error);

        Assert.That(result, Is.EqualTo(PayloadAttributesValidationResult.Success));
        Assert.That(error, Is.Null);
    }

    [TestCase(false, PayloadAttributesVersions.V1)]
    [TestCase(true, PayloadAttributesVersions.V4)]
    public void GetVersion_infers_correct_version_from_present_fields(
        bool hasSlotNumber, int expectedVersion)
    {
        PayloadAttributes attrs = new()
        {
            Timestamp = 1_000UL,
            SlotNumber = hasSlotNumber ? 1UL : null,
        };

        Assert.That(attrs.GetVersion(), Is.EqualTo(expectedVersion));
    }
}
