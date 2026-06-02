// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
public class WithdrawalValidatorTests
{
    [TestCaseSource(nameof(WithdrawalValidationCases))]
    [MaxTime(Timeout.MaxTestTime)]
    public bool Withdrawal_validation(IReleaseSpec spec, Withdrawal[]? withdrawals, Hash256? withdrawalRoot)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, spec));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        BlockBuilder blockBuilder = Build.A.Block.WithParent(parent);
        if (withdrawals is not null)
            blockBuilder = blockBuilder.WithWithdrawals(withdrawals);
        if (withdrawalRoot is not null)
            blockBuilder = blockBuilder.WithWithdrawalsRoot(withdrawalRoot);

        return blockValidator.ValidateSuggestedBlock(blockBuilder.TestObject, parent, out _);
    }

    private static IEnumerable<TestCaseData> WithdrawalValidationCases()
    {
        Withdrawal[] twoWithdrawals = [TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth];
        Withdrawal[] empty = [];

        yield return new TestCaseData(London.Instance, twoWithdrawals, null)
        {
            TestName = "Not_null_withdrawals_are_invalid_pre_shanghai",
            ExpectedResult = false
        };
        yield return new TestCaseData(Shanghai.Instance, null, null)
        {
            TestName = "Null_withdrawals_are_invalid_post_shanghai",
            ExpectedResult = false
        };
        yield return new TestCaseData(Shanghai.Instance, twoWithdrawals, TestItem.KeccakD)
        {
            TestName = "Withdrawals_with_incorrect_withdrawals_root_are_invalid",
            ExpectedResult = false
        };
        yield return new TestCaseData(Shanghai.Instance, empty, new WithdrawalTrie(empty).RootHash)
        {
            TestName = "Empty_withdrawals_are_valid_post_shanghai",
            ExpectedResult = true
        };
        yield return new TestCaseData(Shanghai.Instance, twoWithdrawals, new WithdrawalTrie(twoWithdrawals).RootHash)
        {
            TestName = "Correct_withdrawals_block_post_shanghai",
            ExpectedResult = true
        };
    }
}
