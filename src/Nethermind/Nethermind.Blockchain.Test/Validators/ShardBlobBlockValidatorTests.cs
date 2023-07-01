// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class ShardBlobBlockValidatorTests
{
    [TestCaseSource(nameof(DataGasFieldsPerForkTestCases))]
    public static bool Data_gas_fields_should_be_set(IReleaseSpec spec, ulong? dataGasUsed, ulong? excessDataGas)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, spec));
        HeaderValidator headerValidator = new(Substitute.For<IBlockTree>(), Always.Valid, specProvider, TestLogManager.Instance);
        BlockValidator blockValidator = new(Always.Valid, headerValidator, Always.Valid, specProvider, TestLogManager.Instance);
        return blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithDataGasUsed(dataGasUsed)
            .WithExcessDataGas(excessDataGas)
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .WithParent(Build.A.BlockHeader.TestObject)
            .TestObject);
    }

    [TestCase(0ul, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock - Eip4844Constants.DataGasPerBlob, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxDataGasPerBlock + Eip4844Constants.DataGasPerBlob, ExpectedResult = false)]
    public bool Blobs_per_block_count_is_valid(ulong dataGasUsed)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, TestLogManager.Instance);
        return blockValidator.ValidateSuggestedBlock(
            Build.A.Block
                .WithWithdrawalsRoot(TestItem.KeccakA)
                .WithWithdrawals(TestItem.WithdrawalA_1Eth)
                .WithDataGasUsed(dataGasUsed)
                .WithExcessDataGas(0)
                .WithTransactions(Enumerable.Range(0, (int)(dataGasUsed / Eip4844Constants.DataGasPerBlob))
                    .Select(i => Build.A.Transaction.WithType(TxType.Blob)
                                                    .WithMaxFeePerDataGas(ulong.MaxValue)
                                                    .WithBlobVersionedHashes(1).TestObject).ToArray())
                .TestObject);
    }

    public static IEnumerable<TestCaseData> DataGasFieldsPerForkTestCases
    {
        get
        {
            yield return new TestCaseData(Shanghai.Instance, null, null)
            {
                TestName = "Data gas fields are not set pre-Cancun",
                ExpectedResult = true
            };
            yield return new TestCaseData(Shanghai.Instance, 0ul, null)
            {
                TestName = "DataGasUsed is set pre-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Shanghai.Instance, null, 0ul)
            {
                TestName = "ExcessDataGas is set pre-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Shanghai.Instance, 0ul, 0ul)
            {
                TestName = "Data gas fields are set pre-Cancun",
                ExpectedResult = false
            };


            yield return new TestCaseData(Cancun.Instance, null, null)
            {
                TestName = "Data gas fields are not set post-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, 0ul, null)
            {
                TestName = "Just DataGasUsed is set post-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, null, 0ul)
            {
                TestName = "Just ExcessDataGas is set post-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, 0ul, 0ul)
            {
                TestName = "Data gas fields are set post-Cancun",
                ExpectedResult = true
            };
        }
    }
}
