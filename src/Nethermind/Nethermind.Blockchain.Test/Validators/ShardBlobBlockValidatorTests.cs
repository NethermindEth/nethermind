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
    [TestCaseSource(nameof(BlobGasFieldsPerForkTestCases))]
    public static bool Blob_gas_fields_should_be_set(IReleaseSpec spec, ulong? blobGasUsed, ulong? excessBlobGas)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, spec));
        HeaderValidator headerValidator = new(Substitute.For<IBlockTree>(), Always.Valid, specProvider, TestLogManager.Instance);
        BlockValidator blockValidator = new(Always.Valid, headerValidator, Always.Valid, specProvider, TestLogManager.Instance);
        return blockValidator.ValidateSuggestedBlock(Build.A.Block
            .WithBlobGasUsed(blobGasUsed)
            .WithExcessBlobGas(excessBlobGas)
            .WithWithdrawalsRoot(TestItem.KeccakA)
            .WithWithdrawals(TestItem.WithdrawalA_1Eth)
            .WithParent(Build.A.BlockHeader.TestObject)
            .TestObject);
    }

    [TestCase(0ul, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxBlobGasPerBlock - Eip4844Constants.BlobGasPerBlob, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxBlobGasPerBlock, ExpectedResult = true)]
    [TestCase(Eip4844Constants.MaxBlobGasPerBlock + Eip4844Constants.BlobGasPerBlob, ExpectedResult = false)]
    public bool Blobs_per_block_count_is_valid(ulong blobGasUsed)
    {
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Cancun.Instance));
        BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, TestLogManager.Instance);
        return blockValidator.ValidateSuggestedBlock(
            Build.A.Block
                .WithWithdrawalsRoot(TestItem.KeccakA)
                .WithWithdrawals(TestItem.WithdrawalA_1Eth)
                .WithBlobGasUsed(blobGasUsed)
                .WithExcessBlobGas(0)
                .WithTransactions(Enumerable.Range(0, (int)(blobGasUsed / Eip4844Constants.BlobGasPerBlob))
                    .Select(i => Build.A.Transaction.WithType(TxType.Blob)
                                                    .WithMaxFeePerBlobGas(ulong.MaxValue)
                                                    .WithBlobVersionedHashes(1).TestObject).ToArray())
                .TestObject);
    }

    public static IEnumerable<TestCaseData> BlobGasFieldsPerForkTestCases
    {
        get
        {
            yield return new TestCaseData(Shanghai.Instance, null, null)
            {
                TestName = "Blob gas fields are not set pre-Cancun",
                ExpectedResult = true
            };
            yield return new TestCaseData(Shanghai.Instance, 0ul, null)
            {
                TestName = "BlobGasUsed is set pre-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Shanghai.Instance, null, 0ul)
            {
                TestName = "ExcessBlobGas is set pre-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Shanghai.Instance, 0ul, 0ul)
            {
                TestName = "Blob gas fields are set pre-Cancun",
                ExpectedResult = false
            };


            yield return new TestCaseData(Cancun.Instance, null, null)
            {
                TestName = "Blob gas fields are not set post-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, 0ul, null)
            {
                TestName = "Just BlobGasUsed is set post-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, null, 0ul)
            {
                TestName = "Just ExcessBlobGas is set post-Cancun",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, 0ul, 0ul)
            {
                TestName = "Blob gas fields are set post-Cancun",
                ExpectedResult = true
            };
        }
    }
}
