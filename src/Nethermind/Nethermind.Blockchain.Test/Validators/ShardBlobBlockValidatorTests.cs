// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
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
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, NullBlockFinder.Instance, LimboLogs.Instance);
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .WithExcessDataGas(1).TestObject, Build.An.EmptyBlockHeader);
        Assert.False(isValid);
    }

    [Test]
    public void Null_ExcessDataGas_is_invalid_post_cancun()
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, NullBlockFinder.Instance, LimboLogs.Instance);
        bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .TestObject, Build.An.EmptyBlockHeader);
        Assert.False(isValid);
    }

    [TestCase(0, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock / Eip4844Constants.DataGasPerBlob - 1, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock / Eip4844Constants.DataGasPerBlob, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock / Eip4844Constants.DataGasPerBlob + 1, ExpectedResult = false)]
    public bool Blobs_per_block_count_is_valid(int blobsCount)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, NullBlockFinder.Instance, LimboLogs.Instance);
        return blockValidator.ValidateSuggestedBlock(
            Build.A.Block
                .WithWithdrawalsRoot(TestItem.KeccakA)
                .WithWithdrawals(TestItem.WithdrawalA_1Eth)
                .WithExcessDataGas(IntrinsicGasCalculator.CalculateExcessDataGas(0, blobsCount, specProvider.GenesisSpec)!.Value)
                .WithTransactions(Build.A.Transaction.WithBlobVersionedHashes(blobsCount).TestObject)
                .TestObject, Build.An.EmptyBlockHeader);
    }
}
