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
using FluentAssertions;

namespace Nethermind.Blockchain.Test.Validators;

public class BlockValidatorTests
{
    private static BlockValidator _blockValidator = null!;

    [SetUp]
    public void Setup()
    {
        IHeaderValidator headerValidator = Substitute.For<IHeaderValidator>();
        headerValidator.Validate(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()).Returns(true);
        _blockValidator = new(
            Substitute.For<ITxValidator>(),
            headerValidator,
            Substitute.For<IUnclesValidator>(),
            Substitute.For<ISpecProvider>(),
            LimboLogs.Instance);
    }


    [Test]
    public void Accepts_valid_block()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(header).WithEncodedSize(Eip7934Constants.DefaultMaxRlpBlockSize).TestObject;
        bool result = _blockValidator.ValidateSuggestedBlock(block, header, out _);
        Assert.That(result, Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_more_uncles_than_allowed_returns_false()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ReleaseSpec releaseSpec = new()
        {
            MaximumUncleCount = 0
        };
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, releaseSpec));

        BlockValidator blockValidator = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        bool noiseRemoved = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithParent(parent).TestObject, parent, out _);
        Assert.That(noiseRemoved, Is.True);

        bool result = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithParent(parent).WithUncles(Build.A.BlockHeader.TestObject).TestObject, parent, out _);
        Assert.That(result, Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_do_not_check_uncle_when_orphaned()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = new TestSpecProvider(Frontier.Instance);

        BlockValidator blockValidator = new(txValidator, Always.Valid, Always.Invalid, specProvider, LimboLogs.Instance);

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block
            .WithParent(parent)
            .WithUncles(Build.A.BlockHeader.WithNumber(10).TestObject)
            .TestObject;
        blockValidator.ValidateSuggestedBlock(block, parent, out _).Should().Be(false);
        blockValidator.ValidateOrphanedBlock(block, out _).Should().Be(true);
    }

    [Test]
    public void ValidateBodyAgainstHeader_BlockIsValid_ReturnsTrue()
    {
        Block block = Build.A.Block
            .WithTransactions(1, Substitute.For<IReleaseSpec>())
            .WithWithdrawals(1)
            .TestObject;


        Assert.That(
            _blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
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
            _blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
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
            _blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
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
            _blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body),
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

        sut.ValidateProcessedBlock(
            suggestedBlock,
            [],
            processedBlock, out string? error);

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

        sut.ValidateProcessedBlock(
            suggestedBlock,
            [],
            processedBlock, out string? error);

        Assert.That(error, Does.StartWith("InvalidStateRoot"));
    }

    [Test]
    public void ValidateProcessedBlock_ReceiptCountMismatch_DoesNotThrow()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block
            .WithStateRoot(Keccak.Zero)
            .WithTransactions(2, specProvider)
            .TestObject;

        Assert.DoesNotThrow(() => sut.ValidateProcessedBlock(
            processedBlock,
            [],
            suggestedBlock));
    }

    [Test]
    public void ValidateProcessedBlock_ReceiptCountMismatch_ReturnsFalse()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block
            .WithStateRoot(Keccak.Zero)
            .WithTransactions(3, specProvider)
            .TestObject;

        bool result = sut.ValidateProcessedBlock(
            processedBlock,
            [Build.A.Receipt.TestObject],
            suggestedBlock);

        Assert.That(result, Is.False);
    }

    private static IEnumerable<TestCaseData> BadSuggestedBlocks()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        yield return new TestCaseData(
            Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parent).WithUnclesHash(Keccak.Zero).TestObject)
                .TestObject,
            parent,
            Substitute.For<ISpecProvider>(),
            "InvalidUnclesHash");

        yield return new TestCaseData(
            Build.A.Block
                .WithHeader(Build.A.BlockHeader.WithParent(parent).WithTransactionsRoot(Keccak.Zero).TestObject)
                .TestObject,
            parent,
            Substitute.For<ISpecProvider>(),
            "InvalidTxRoot");

        yield return new TestCaseData(
            Build.A.Block.WithBlobGasUsed(131072)
                .WithParent(parent)
                .WithExcessBlobGas(1)
                .WithTransactions(
                    Build.A.Transaction.WithShardBlobTxTypeAndFields(1)
                        .WithMaxFeePerBlobGas(0)
                        .WithMaxFeePerGas(1000000)
                        .Signed()
                        .TestObject)
                .TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Cancun.Instance)),
            "InsufficientMaxFeePerBlobGas");

        yield return new TestCaseData(
            Build.A.Block.WithParent(parent).WithEncodedSize(Eip7934Constants.DefaultMaxRlpBlockSize + 1).TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Osaka.Instance)),
            "ExceededBlockSizeLimit");
    }

    [TestCaseSource(nameof(BadSuggestedBlocks))]
    public void ValidateSuggestedBlock_SuggestedBlockIsInvalid_CorrectErrorIsSet(Block suggestedBlock, BlockHeader parent, ISpecProvider specProvider, string expectedError)
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);

        sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        Assert.That(error, Does.StartWith(expectedError));
    }
}
