// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBlockValidatorTests
{
    private static readonly Hash256 TestWithdrawalsRoot =
        new("0x1234567890123456789012345678901234567890123456789012345678901234");

    private static IEnumerable<TestCaseData> WithdrawalsRootTestCases()
    {
        // Pre-Canyon: WithdrawalsRoot should be null
        yield return new TestCaseData(Spec.CanyonTimestamp - 1, null, true)
            .SetName("Pre-Canyon: Valid null withdrawals root");
        yield return new TestCaseData(Spec.CanyonTimestamp - 1, TestWithdrawalsRoot, false)
            .SetName("Pre-Canyon: Invalid non-null withdrawals root");

        // Canyon: WithdrawalsRoot should be Keccak.EmptyTreeHash
        yield return new TestCaseData(Spec.CanyonTimestamp, Keccak.EmptyTreeHash, true)
            .SetName("Canyon: Valid empty tree hash withdrawals root");
        yield return new TestCaseData(Spec.CanyonTimestamp, null, false)
            .SetName("Canyon: Invalid null withdrawals root");
        yield return new TestCaseData(Spec.CanyonTimestamp, TestWithdrawalsRoot, false)
            .SetName("Canyon: Invalid non-empty tree hash withdrawals root");

        // Isthmus: WithdrawalsRoot should be 32 bytes non-null
        yield return new TestCaseData(Spec.IsthmusTimeStamp, TestWithdrawalsRoot, true)
            .SetName("Isthmus: Valid non-null withdrawals root");
        yield return new TestCaseData(Spec.IsthmusTimeStamp, null, false)
            .SetName("Isthmus: Invalid null withdrawals root");
        yield return new TestCaseData(Spec.IsthmusTimeStamp, Keccak.EmptyTreeHash, false)
            .SetName("Isthmus: Invalid withdrawals root of an empty tree");
    }

    [TestCaseSource(nameof(WithdrawalsRootTestCases))]
    public void ValidateSuggestedBlock_ValidateWithdrawalsRoot(ulong timestamp, Hash256? withdrawalsRoot, bool isValid)
    {
        var specProvider = Substitute.For<ISpecProvider>();
        var specHelper = Substitute.For<IOptimismSpecHelper>();
        specHelper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(timestamp >= Spec.IsthmusTimeStamp);
        specHelper.IsCanyon(Arg.Any<BlockHeader>()).Returns(timestamp >= Spec.CanyonTimestamp);

        var block = Build.A.Block
            .WithWithdrawals(timestamp >= Spec.IsthmusTimeStamp ? [] : null)
            .WithHeader(Build.A.BlockHeader
                .WithTimestamp(timestamp)
                .WithWithdrawalsRoot(withdrawalsRoot)
                .TestObject)
            .TestObject;

        var validator = new OptimismBlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            specProvider,
            specHelper,
            TestLogManager.Instance);

        var result = validator.ValidateSuggestedBlock(block, out string? error);

        result.Should().Be(isValid);
        if (!isValid)
        {
            error.Should().NotBeNull();
        }
    }

    private static IEnumerable<TestCaseData> WithdrawalsListTestCases()
    {
        yield return new TestCaseData(Array.Empty<Withdrawal>(), true)
            .SetName("Valid empty withdrawals list");
        yield return new TestCaseData(null, false)
            .SetName("Invalid null withdrawals list");
        yield return new TestCaseData(new[] { TestItem.WithdrawalA_1Eth }, false)
            .SetName("Invalid non-empty withdrawals list");
    }

    [TestCaseSource(nameof(WithdrawalsListTestCases))]
    public void ValidateSuggestedBlock_ValidateWithdrawalsList_PostIsthmus(Withdrawal[]? withdrawals, bool isValid)
    {
        var specProvider = Substitute.For<ISpecProvider>();
        var specHelper = Substitute.For<IOptimismSpecHelper>();
        specHelper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(true);
        specHelper.IsCanyon(Arg.Any<BlockHeader>()).Returns(true);

        var block = Build.A.Block
            .WithWithdrawals(withdrawals)
            .WithHeader(Build.A.BlockHeader
                .WithTimestamp(Spec.IsthmusTimeStamp)
                .WithWithdrawalsRoot(TestWithdrawalsRoot)
                .TestObject)
            .TestObject;

        var validator = new OptimismBlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            specProvider,
            specHelper,
            TestLogManager.Instance);

        var result = validator.ValidateSuggestedBlock(block, out string? error);

        result.Should().Be(isValid);
        if (!isValid)
        {
            error.Should().NotBeNull();
        }
    }
}
