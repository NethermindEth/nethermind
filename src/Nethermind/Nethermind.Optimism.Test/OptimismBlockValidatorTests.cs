// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
[TestFixtureSource(typeof(Fork), nameof(Fork.AllAndNextToGenesis))]
public class OptimismBlockValidatorTests(Fork fork)
{
    private readonly ulong _timestamp = fork.Timestamp;

    private Hash256? GetWithdrawalsRoot() => _timestamp switch
    {
        >= Spec.IsthmusTimeStamp => TestWithdrawalsRoot,
        >= Spec.CanyonTimestamp => Keccak.EmptyTreeHash,
        _ => null
    };

    private static readonly Hash256 TestWithdrawalsRoot =
        new("0x1234567890123456789012345678901234567890123456789012345678901234");

    private (BlockHeader parentHeader, Block header) BuildBlock(Action<BlockBuilder>? postBuild = null)
    {
        var parentBlock = Build.A.BlockHeader.TestObject;
        var builder = Build.A.Block
            .WithWithdrawals(_timestamp >= Spec.IsthmusTimeStamp ? [] : null)
            .WithHeader(Build.A.BlockHeader
                .WithParent(parentBlock)
                .WithTimestamp(_timestamp)
                .WithBlobGasUsed(0)
                .WithWithdrawalsRoot(GetWithdrawalsRoot())
                .TestObject);

        postBuild?.Invoke(builder);

        return (parentBlock, builder.TestObject);
    }

    private static IEnumerable<TestCaseData> WithdrawalsRootTestCases()
    {
        // Pre-Canyon: WithdrawalsRoot should be null
        yield return new TestCaseData(null, Valid.Before(Spec.CanyonTimestamp))
            .SetName("Null withdrawals root");

        // Canyon: WithdrawalsRoot should be Keccak.EmptyTreeHash
        yield return new TestCaseData(Keccak.EmptyTreeHash, Valid.Between(Spec.CanyonTimestamp, Spec.IsthmusTimeStamp))
            .SetName("Empty tree hash withdrawals root");

        // Isthmus: WithdrawalsRoot should be 32 bytes non-null
        yield return new TestCaseData(TestWithdrawalsRoot, Valid.Since(Spec.IsthmusTimeStamp))
            .SetName("Non-null withdrawals root");
    }

    [TestCaseSource(nameof(WithdrawalsRootTestCases))]
    public void ValidateSuggestedBlock_ValidateWithdrawalsRoot(Hash256? withdrawalsRoot, Valid isValid)
    {
        (BlockHeader parentHeader, Block block) = BuildBlock(b => b
            .WithWithdrawalsRoot(withdrawalsRoot)
        );

        var validator = new OptimismBlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            Spec.BuildFor(block.Header),
            Spec.Instance,
            TestLogManager.Instance);

        Assert.That(
            validator.ValidateSuggestedBlock(block, parentHeader, out string? error),
            Is.EqualTo(isValid.On(_timestamp)),
            () => error!);
    }

    private static IEnumerable<TestCaseData> WithdrawalsListTestCases()
    {
        yield return new TestCaseData(Array.Empty<Withdrawal>(), Valid.Always)
            .SetName("Empty withdrawals list");
        yield return new TestCaseData(null, Valid.Before(Spec.IsthmusTimeStamp))
            .SetName("Null withdrawals list");
        yield return new TestCaseData(new[] { TestItem.WithdrawalA_1Eth }, Valid.Before(Spec.IsthmusTimeStamp))
            .SetName("Non-empty withdrawals list");
    }

    [TestCaseSource(nameof(WithdrawalsListTestCases))]
    public void ValidateSuggestedBlock_ValidateWithdrawalsList(Withdrawal[]? withdrawals, Valid isValid)
    {
        (BlockHeader parentHeader, Block block) = BuildBlock(b => b
            .WithWithdrawals(withdrawals)
            .WithWithdrawalsRoot(GetWithdrawalsRoot())
        );

        var validator = new OptimismBlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            Spec.BuildFor(block.Header),
            Spec.Instance,
            TestLogManager.Instance);

        Assert.That(
            validator.ValidateSuggestedBlock(block, parentHeader, out string? error),
            Is.EqualTo(isValid.On(_timestamp)),
            () => error!);
    }

    private static IEnumerable<TestCaseData> BlobGasUsedTestCases()
    {
        yield return new TestCaseData(null, Valid.Since(Spec.EcotoneTimestamp))
            .SetName("Null blob gas used");
        yield return new TestCaseData(0, Valid.Always)
            .SetName("Zero blob gas used");
        yield return new TestCaseData(10_000, Valid.Since(Spec.EcotoneTimestamp))
            .SetName("Positive blob gas used");
    }

    [TestCaseSource(nameof(BlobGasUsedTestCases))]
    public void ValidateSuggestedBlock_ValidatesBlobGasUsed(int? blobGasUsed, Valid isValid)
    {
        (BlockHeader parentHeader, Block block) = BuildBlock(b => b
            .WithBlobGasUsed((ulong?)blobGasUsed)
        );

        var validator = new OptimismBlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            Spec.BuildFor(block.Header),
            Spec.Instance,
            TestLogManager.Instance);

        Assert.That(
            validator.ValidateSuggestedBlock(block, parentHeader, out string? error),
            Is.EqualTo(isValid.On(_timestamp)),
            () => error!);
    }
}
