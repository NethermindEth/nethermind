// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
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
    [TestCaseSource(nameof(DataGasFieldsPerForkTestCases))]
    public static bool Data_gas_fields_should_be_set(IReleaseSpec spec, ulong? dataGasUsed, ulong? excessDataGas)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, spec));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        return blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithDataGasUsed(dataGasUsed)
            .WithExcessDataGas(excessDataGas)
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .TestObject);
    }

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
                .WithDataGasUsed(dataGasUsed)
                .WithTransactions(Enumerable.Range(0, (int)(dataGasUsed / Eip4844Constants.DataGasPerBlob))
                    .Select(i => Build.A.Transaction.WithBlobVersionedHashes(1).TestObject).ToArray())
                .TestObject);
    }

    public static IEnumerable<TestCaseData> DataGasFieldsPerForkTestCases
    {
        get
        {
            yield return new TestCaseData(Shanghai.Instance, null, null)
            {
                TestName = "Not set pre-Cancun",
                ExpectedResult = true
            };
            yield return new TestCaseData(Shanghai.Instance, 1ul, null)
            {
                TestName = "DataGasUsed is improperly set pre-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Shanghai.Instance, null, 1ul)
            {
                TestName = "ExcessDataGas is improperly set pre-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Shanghai.Instance, 1ul, 1ul)
            {
                TestName = "Data gas field are improperly set pre-Cancun",
                ExpectedResult = false
            };


            yield return new TestCaseData(Cancun.Instance, null, null)
            {
                TestName = "Not set post-Cancun",
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
            yield return new TestCaseData(Cancun.Instance, 1ul, 1ul)
            {
                TestName = "Data gas fields are set post-Cancun",
                ExpectedResult = true
            };
        }
    }
}
