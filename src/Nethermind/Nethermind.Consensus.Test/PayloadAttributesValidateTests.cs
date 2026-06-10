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
    private static PayloadAttributes ValidV3Attributes(ulong timestamp = 1_000UL) => new()
    {
        Timestamp = timestamp,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = Address.Zero,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = null,
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

    [Test]
    public void Validate_returns_specific_field_error_when_V4_fields_absent_on_Amsterdam_timestamp()
    {
        ISpecProvider sp = MakeSpecProvider(isAmsterdam: true);
        PayloadAttributes attrs = ValidV3Attributes();

        PayloadAttributesValidationResult result = attrs.Validate(sp, fcuVersion: PayloadAttributesVersions.V4, out string error);

        Assert.That(result, Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes));
        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("must be provided"),
            "Error should identify which V4 field is missing, not emit a generic version-mismatch message.");
        Assert.That(error, Does.Not.Contain("expected"),
            "A 'V4 expected' version-mismatch error would obscure the real problem (missing field).");
    }

    [Test]
    public void Validate_succeeds_for_complete_V4_attributes_on_Amsterdam_timestamp()
    {
        ISpecProvider sp = MakeSpecProvider(isAmsterdam: true);
        PayloadAttributes attrs = ValidV3Attributes();
        attrs.SlotNumber = 42UL;

        PayloadAttributesValidationResult result = attrs.Validate(sp, fcuVersion: PayloadAttributesVersions.V4, out string error);

        Assert.That(result, Is.EqualTo(PayloadAttributesValidationResult.Success));
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_returns_UnsupportedFork_when_V4_attrs_sent_to_V3_spec()
    {
        ISpecProvider sp = MakeSpecProvider(isAmsterdam: false);
        PayloadAttributes attrs = ValidV3Attributes();
        attrs.SlotNumber = 42UL;

        PayloadAttributesValidationResult result = attrs.Validate(sp, fcuVersion: PayloadAttributesVersions.V3, out string error);

        Assert.That(result, Is.EqualTo(PayloadAttributesValidationResult.UnsupportedFork));
        Assert.That(error, Is.Not.Null);
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
