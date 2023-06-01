// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class ShardBlobBlockValidatorTests
{
    [Test]
    public void Not_null_ExcessDataGas_is_invalid_pre_cancun()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Shanghai.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .WithExcessDataGas(1).TestObject);
        Assert.False(isValid);
    }

    [Test]
    public void Null_ExcessDataGas_is_invalid_post_cancun()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .TestObject);
        Assert.False(isValid);
    }

    //[Test]
    //public void Not_null_ExcessDataGas_is_invalid_pre_cancun()
    //{
    //    ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Shanghai.Instance));
    //    BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
    //    bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
    //        .WithWithdrawalsRoot(TestItem.KeccakA)
    //        .WithWithdrawals(TestItem.WithdrawalA_1Eth)
    //        .WithExcessDataGas(1).TestObject);
    //    Assert.False(isValid);
    //}

    //[Test]
    //public void Null_ExcessDataGas_is_invalid_post_cancun()
    //{
    //    ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
    //    BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
    //    bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
    //        .WithWithdrawalsRoot(TestItem.KeccakA)
    //        .WithWithdrawals(TestItem.WithdrawalA_1Eth)
    //        .TestObject);
    //    Assert.False(isValid);
    //}

    //[Test]
    //public void Null_ExcessDataGas_is_invalid_post_cancun()
    //{
    //    TestInvalid(Cancun.Instance, b => b.WithExcessDataGas(0).WithDataGasUsed(0));
    //    TestInvalid(Cancun.Instance, b => b.WithExcessDataGas(0).WithDataGasUsed(0));
    //    TestInvalid(Cancun.Instance, b => b.WithExcessDataGas(0).WithDataGasUsed(0));
    //    TestInvalid(Cancun.Instance, b => b.WithExcessDataGas(0).WithDataGasUsed(0));
    //    TestInvalid(Cancun.Instance, b => b.WithExcessDataGas(0).WithDataGasUsed(0));
    //}
    //private static void TestInvalid(IReleaseSpec spec, Action<BlockHeaderBuilder> with)
    //{
    //    ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
    //    BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
    //    bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
    //        .WithWithdrawalsRoot(TestItem.KeccakA)
    //        .WithWithdrawals(TestItem.WithdrawalA_1Eth)
    //        .TestObject);
    //    Assert.That(isValid, Is.EqualTo());
    //}

    [TestCase(0ul, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock - Eip4844Constants.DataGasPerBlob, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock + Eip4844Constants.DataGasPerBlob, ExpectedResult = false)]
    public bool Blobs_per_block_count_is_valid(ulong dataGasUsed)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        return blockValidator.ValidateSuggestedBlock(
            Build.A.Block
                .WithWithdrawalsRoot(TestItem.KeccakA)
                .WithWithdrawals(TestItem.WithdrawalA_1Eth)
                .WithTransactions(Enumerable.Range(0, (int)(dataGasUsed / Eip4844Constants.DataGasPerBlob))
                    .Select(i => Build.A.Transaction.WithBlobVersionedHashes(1).TestObject).ToArray())
                .TestObject);
    }
}
