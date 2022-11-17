// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

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

public class WithdrawalValidatorTests
{
    [Test]
    public void Not_null_withdrawals_are_invalid_pre_shanghai()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, London.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithWithdrawals(new Withdrawal[] { TestItem.WithdrawalA, TestItem.WithdrawalB }).TestObject);
        Assert.False(isValid);
    }

    [Test]
    public void Null_withdrawals_are_invalid_post_shanghai()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Shanghai.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block.TestObject);
        Assert.False(isValid);
    }

    [Test]
    public void Withdrawals_with_incorrect_withdrawals_root_are_invalid()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Shanghai.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Withdrawal[] withdrawals = { TestItem.WithdrawalA, TestItem.WithdrawalB };
        Keccak withdrawalRoot = new WithdrawalTrie(withdrawals).RootHash;
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithWithdrawals(withdrawals).WithWithdrawalsRoot(TestItem.KeccakD).TestObject);
        Assert.False(isValid);
    }

    [Test]
    public void Empty_withdrawals_are_valid_post_shanghai()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Shanghai.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Withdrawal[] withdrawals = {};
        Keccak withdrawalRoot = new WithdrawalTrie(withdrawals).RootHash;
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithWithdrawals(withdrawals).WithWithdrawalsRoot(withdrawalRoot).TestObject);
        Assert.True(isValid);
    }

    [Test]
    public void Correct_withdrawals_block_post_shanghai()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Shanghai.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Withdrawal[] withdrawals = { TestItem.WithdrawalA, TestItem.WithdrawalB };
        Keccak withdrawalRoot = new WithdrawalTrie(withdrawals).RootHash;
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithWithdrawals(withdrawals).WithWithdrawalsRoot(withdrawalRoot).TestObject);
        Assert.True(isValid);
    }
}
