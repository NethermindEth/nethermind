// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;

namespace Nethermind.Blockchain.Test.Validators;

public class BlockValidatorTests
{
    private static readonly BlockValidator BlockValidator =
        new BlockValidator(
            Substitute.For<ITxValidator>(),
            Substitute.For<IHeaderValidator>(),
            Substitute.For<IUnclesValidator>(),
            Substitute.For<ISpecProvider>(),
            LimboLogs.Instance);


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_more_uncles_than_allowed_returns_false()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ReleaseSpec releaseSpec = new();
        releaseSpec.MaximumUncleCount = 0;
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, releaseSpec));

        BlockValidator blockValidator = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        bool noiseRemoved = blockValidator.ValidateSuggestedBlock(Build.A.Block.TestObject);
        Assert.That(noiseRemoved, Is.True);

        bool result = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithUncles(Build.A.BlockHeader.TestObject).TestObject);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ValidateBodyAgainstHeader_BlockIsValid_ReturnsTrue()
    {
        Block block = Build.A.Block
            .WithTransactions(1, Substitute.For<IReleaseSpec>())
            .WithWithdrawals(1)
            .TestObject;


        Assert.That(
            BlockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
            Is.True);
    }

    [Test]
    public void ValidateBodyAgainstHeader_BlockHasInvalidTxRoot_ReturnsFalse()
    {
        Block block = Build.A.Block
            .WithTransactions(1, Substitute.For<IReleaseSpec>())
            .WithWithdrawals(1)
            .TestObject;
        block.Header.TxRoot = Keccak.OfAnEmptyString;

        Assert.That(
            BlockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
            Is.False);
    }


    [Test]
    public void ValidateBodyAgainstHeader_BlockHasInvalidUnclesRoot_ReturnsFalse()
    {
        Block block = Build.A.Block
            .WithTransactions(1, Substitute.For<IReleaseSpec>())
            .WithWithdrawals(1)
            .TestObject;
        block.Header.UnclesHash = Keccak.OfAnEmptyString;

        Assert.That(
            BlockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
            Is.False);
    }

    [Test]
    public void ValidateBodyAgainstHeader_BlockHasInvalidWithdrawalsRoot_ReturnsFalse()
    {
        Block block = Build.A.Block
            .WithTransactions(1, Substitute.For<IReleaseSpec>())
            .WithWithdrawals(1)
            .TestObject;
        block.Header.WithdrawalsRoot = Keccak.OfAnEmptyString;

        Assert.That(
            BlockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
            Is.False);
    }

    [Test]
    public void ValidateProcessedBlock_HashesAreTheSame_ReturnsTrue()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block.TestObject;

        Assert.That(sut.ValidateProcessedBlock(
            suggestedBlock,
            [],
            processedBlock), Is.True);
    }

    [Test]
    public void ValidateProcessedBlock_HashesAreTheSame_ErrorIsNull()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block.TestObject;
        string? error;

        sut.ValidateProcessedBlock(
            suggestedBlock,
            [],
            processedBlock, out error);

        Assert.That(error, Is.Null);
    }

    [Test]
    public void ValidateProcessedBlock_StateRootIsWrong_ReturnsFalse()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block.WithStateRoot(Keccak.Zero).TestObject;

        Assert.That(sut.ValidateProcessedBlock(
            suggestedBlock,
            [],
            processedBlock), Is.False);
    }

    [Test]
    public void ValidateProcessedBlock_StateRootIsWrong_ErrorIsSet()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block.WithStateRoot(Keccak.Zero).TestObject;
        string? error;

        sut.ValidateProcessedBlock(
            suggestedBlock,
            [],
            processedBlock, out error);

        Assert.That(error, Does.StartWith("InvalidStateRoot"));
    }

    private static IEnumerable<TestCaseData> BadSuggestedBlocks()
    {
        yield return new TestCaseData(
        Build.A.Block.WithHeader(Build.A.BlockHeader.WithUnclesHash(Keccak.Zero).TestObject).TestObject,
        Substitute.For<ISpecProvider>(),
        "InvalidUnclesHash");

        yield return new TestCaseData(
        Build.A.Block.WithHeader(Build.A.BlockHeader.WithTransactionsRoot(Keccak.Zero).TestObject).TestObject,
        Substitute.For<ISpecProvider>(),
        "InvalidTxRoot");

        yield return new TestCaseData(
        Build.A.Block.WithBlobGasUsed(131072)
        .WithExcessBlobGas(1)
        .WithTransactions(
            Build.A.Transaction.WithShardBlobTxTypeAndFields(1)
            .WithMaxFeePerBlobGas(0)
            .WithMaxFeePerGas(1000000)
            .Signed()
            .TestObject)
        .TestObject,
        new CustomSpecProvider(((ForkActivation)0, Cancun.Instance)),
        "InsufficientMaxFeePerBlobGas");
    }

    [TestCaseSource(nameof(BadSuggestedBlocks))]
    public void ValidateSuggestedBlock_SuggestedBlockIsInvalid_CorrectErrorIsSet(Block suggestedBlock, ISpecProvider specProvider, string expectedError)
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        string? error;

        sut.ValidateSuggestedBlock(
            suggestedBlock, out error);

        Assert.That(error, Does.StartWith(expectedError));
    }
}
