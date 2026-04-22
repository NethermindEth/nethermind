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
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
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

    private static IEnumerable<TestCaseData> CorruptedBodyFieldCases()
    {
        yield return new TestCaseData(null)
            .Returns(true).SetName("ValidateBodyAgainstHeader_BlockIsValid_ReturnsTrue");
        yield return new TestCaseData(new Action<BlockHeader>(h => h.TxRoot = Keccak.OfAnEmptyString))
            .Returns(false).SetName("ValidateBodyAgainstHeader_BlockHasInvalidTxRoot_ReturnsFalse");
        yield return new TestCaseData(new Action<BlockHeader>(h => h.UnclesHash = Keccak.OfAnEmptyString))
            .Returns(false).SetName("ValidateBodyAgainstHeader_BlockHasInvalidUnclesRoot_ReturnsFalse");
        yield return new TestCaseData(new Action<BlockHeader>(h => h.WithdrawalsRoot = Keccak.OfAnEmptyString))
            .Returns(false).SetName("ValidateBodyAgainstHeader_BlockHasInvalidWithdrawalsRoot_ReturnsFalse");
    }

    [TestCaseSource(nameof(CorruptedBodyFieldCases))]
    public bool ValidateBodyAgainstHeader_WithCorruptedField(Action<BlockHeader>? corrupt)
    {
        Block block = Build.A.Block
            .WithTransactions(1, ReleaseSpecSubstitute.Create())
            .WithWithdrawals(1)
            .TestObject;

        corrupt?.Invoke(block.Header);

        return _blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body);
    }

    [TestCase(false, ExpectedResult = true, TestName = "ValidateProcessedBlock_HashesAreTheSame_ReturnsTrue")]
    [TestCase(true, ExpectedResult = false, TestName = "ValidateProcessedBlock_StateRootIsWrong_ReturnsFalse")]
    public bool ValidateProcessedBlock_Returns(bool wrongStateRoot)
    {
        BlockValidator sut = CreateProcessedBlockValidator();
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = wrongStateRoot
            ? Build.A.Block.WithStateRoot(Keccak.Zero).TestObject
            : Build.A.Block.TestObject;

        return sut.ValidateProcessedBlock(suggestedBlock, [], processedBlock);
    }

    [TestCase(false, null, TestName = "ValidateProcessedBlock_HashesAreTheSame_ErrorIsNull")]
    [TestCase(true, "InvalidStateRoot", TestName = "ValidateProcessedBlock_StateRootIsWrong_ErrorIsSet")]
    public void ValidateProcessedBlock_ErrorMessage(bool wrongStateRoot, string? expectedErrorPrefix)
    {
        BlockValidator sut = CreateProcessedBlockValidator();
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = wrongStateRoot
            ? Build.A.Block.WithStateRoot(Keccak.Zero).TestObject
            : Build.A.Block.TestObject;

        sut.ValidateProcessedBlock(suggestedBlock, [], processedBlock, out string? error);

        if (expectedErrorPrefix is null)
            Assert.That(error, Is.Null);
        else
            Assert.That(error, Does.StartWith(expectedErrorPrefix));
    }

    [TestCase(2, 0, TestName = "ValidateProcessedBlock_ReceiptCountMismatch_DoesNotThrow")]
    [TestCase(3, 1, TestName = "ValidateProcessedBlock_ReceiptCountMismatch_ReturnsFalse")]
    public void ValidateProcessedBlock_ReceiptCountMismatch(int txCount, int receiptCount)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockValidator sut = CreateProcessedBlockValidator();
        Block suggestedBlock = Build.A.Block.TestObject;
        Block processedBlock = Build.A.Block
            .WithStateRoot(Keccak.Zero)
            .WithTransactions(txCount, specProvider)
            .TestObject;

        TxReceipt[] receipts = Enumerable.Range(0, receiptCount)
            .Select(_ => Build.A.Receipt.TestObject).ToArray();

        bool result = sut.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock);

        Assert.That(result, Is.False);
    }

    private static BlockValidator CreateProcessedBlockValidator()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        return new BlockValidator(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
    }

    private static IEnumerable<TestCaseData> BadSuggestedBlocks()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        yield return new TestCaseData(
            Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parent).WithUnclesHash(Keccak.Zero).TestObject)
                .TestObject,
            parent,
            Substitute.For<ISpecProvider>(),
            "InvalidUnclesHash")
        { TestName = "InvalidUnclesHash" };

        yield return new TestCaseData(
            Build.A.Block
                .WithHeader(Build.A.BlockHeader.WithParent(parent).WithTransactionsRoot(Keccak.Zero).TestObject)
                .TestObject,
            parent,
            Substitute.For<ISpecProvider>(),
            "InvalidTxRoot")
        { TestName = "InvalidTxRoot" };

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
            "InsufficientMaxFeePerBlobGas")
        { TestName = "InsufficientMaxFeePerBlobGas" };

        yield return new TestCaseData(
            Build.A.Block.WithParent(parent).WithEncodedSize(Eip7934Constants.DefaultMaxRlpBlockSize + 1).TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Osaka.Instance)),
            "ExceededBlockSizeLimit")
        { TestName = "ExceededBlockSizeLimit" };

        yield return new TestCaseData(
            Build.A.Block
                .WithParent(parent)
                .WithBlobGasUsed(0)
                .WithWithdrawals([])
                .WithBlockAccessList(new())
                .WithEncodedBlockAccessList(Rlp.Encode(new BlockAccessList()).Bytes).TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Amsterdam.Instance)),
            "InvalidBlockLevelAccessListHash")
        { TestName = "InvalidBlockLevelAccessListHash" };

        yield return new TestCaseData(
            Build.A.Block
                .WithParent(parent)
                .WithBlobGasUsed(0)
                .WithWithdrawals([])
                .WithBlockAccessList(new())
                .WithEncodedBlockAccessList([0xfa]).TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Amsterdam.Instance)),
            "InvalidBlockLevelAccessList")
        { TestName = "InvalidBlockLevelAccessList" };

        yield return new TestCaseData(
            Build.A.Block
                .WithParent(parent)
                .WithBlobGasUsed(0)
                .WithWithdrawals([])
                .WithBlockAccessList(new()).TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Osaka.Instance)),
            "BlockLevelAccessListNotEnabled")
        { TestName = "BlockLevelAccessListNotEnabled" };
    }

    [TestCaseSource(nameof(BadSuggestedBlocks))]
    public void ValidateSuggestedBlock_SuggestedBlockIsInvalid_CorrectErrorIsSet(Block suggestedBlock, BlockHeader parent, ISpecProvider specProvider, string expectedError)
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);

        sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        Assert.That(error, Does.StartWith(expectedError));
    }

    [TestCase(30_000, true)]
    [TestCase(29_999, false)]
    public void ValidateSuggestedBlock_Enforces_bal_item_gas_limit_boundary(long gasLimit, bool expectedValid)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockAccessList bal = Build.A.BlockAccessList.WithPrecompileChanges(parent.Hash!, timestamp: 12).TestObject;
        byte[] encodedBal = Rlp.Encode(bal).Bytes;
        Hash256 balHash = new(ValueKeccak.Compute(encodedBal).Bytes);
        Block suggestedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(gasLimit)
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBlockAccessList(bal)
            .WithEncodedBlockAccessList(encodedBal)
            .WithBlockAccessListHash(balHash)
            .TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, new CustomSpecProvider(((ForkActivation)0, Amsterdam.Instance)), LimboLogs.Instance);

        bool isValid = sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        Assert.That(isValid, Is.EqualTo(expectedValid));
        if (expectedValid)
        {
            Assert.That(error, Is.Null);
        }
        else
        {
            Assert.That(error, Does.StartWith("BlockAccessListGasLimitExceeded"));
        }
    }

    [TestCase(30_000, true)]
    [TestCase(29_999, false)]
    public void ValidateProcessedBlock_Enforces_bal_item_gas_limit_boundary_for_rlp_imported_blocks(long gasLimit, bool expectedValid)
    {
        // Hive eels/consume-rlp feeds blocks via RLP, which leaves Block.BlockAccessList null
        // (BlockDecoder does not decode BAL). The pre-execution check in
        // ValidateBlockLevelAccessList is gated on a non-null BAL and therefore skipped, so
        // ValidateProcessedBlock must catch the floor against the BAL produced during execution.
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockAccessList bal = Build.A.BlockAccessList.WithPrecompileChanges(parent.Hash!, timestamp: 12).TestObject;
        byte[] encodedBal = Rlp.Encode(bal).Bytes;
        Hash256 balHash = new(ValueKeccak.Compute(encodedBal).Bytes);

        Block suggestedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(gasLimit)
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBlockAccessListHash(balHash)
            .TestObject;

        Block processedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(gasLimit)
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBlockAccessList(bal)
            .WithEncodedBlockAccessList(encodedBal)
            .WithBlockAccessListHash(balHash)
            .TestObject;
        processedBlock.GeneratedBlockAccessList = bal;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        BlockValidator sut = new(txValidator, Always.Valid, Always.Valid, new CustomSpecProvider(((ForkActivation)0, Amsterdam.Instance)), LimboLogs.Instance);

        bool isValid = sut.ValidateProcessedBlock(processedBlock, [], suggestedBlock, out string? error);

        Assert.That(isValid, Is.EqualTo(expectedValid));
        if (expectedValid)
        {
            Assert.That(error, Is.Null);
        }
        else
        {
            Assert.That(error, Does.StartWith("BlockAccessListGasLimitExceeded"));
        }
    }
}
