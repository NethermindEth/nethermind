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
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
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
        Assert.That(blockValidator.ValidateSuggestedBlock(block, parent, out _), Is.EqualTo(false));
        Assert.That(blockValidator.ValidateOrphanedBlock(block, out _), Is.EqualTo(true));
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

    [TestCaseSource(nameof(CorruptedBodyFieldCases))]
    public bool ValidateBodyAgainstHeader_Raw_WithCorruptedField(Action<BlockHeader>? corrupt)
    {
        Block block = Build.A.Block
            .WithTransactions(1, ReleaseSpecSubstitute.Create())
            .WithWithdrawals(1)
            .TestObject;

        corrupt?.Invoke(block.Header);

        using RlpBlockBody rawBody = RlpBlockBody.FromBody(block.Body);
        return _blockValidator.ValidateBodyAgainstHeader(block.Header, rawBody, out _);
    }

    [Test]
    public void ValidateBodyAgainstHeader_Raw_WithMalformedTransaction_ReturnsFalse()
    {
        Block block = Build.A.Block
            .WithTransactions(1, ReleaseSpecSubstitute.Create())
            .TestObject;

        byte[] bodyItem = new byte[BlockBodyDecoder.Instance.GetLength(block.Body, RlpBehaviors.None)];
        RlpWriter writer = new(bodyItem);
        BlockBodyDecoder.Instance.Encode(ref writer, block.Body);
        // Garble the first transaction with a non-canonical length prefix.
        RlpReader reader = new(bodyItem);
        reader.ReadSequenceLength();
        reader.SkipLength();
        bodyItem[reader.Position] = 0xb8;
        bodyItem[reader.Position + 1] = 0x01;

        using RlpBlockBody rawBody = RlpBlockBody.FromBodyItem(System.Buffers.MemoryPool<byte>.Shared.Rent(0), bodyItem);
        Assert.That(_blockValidator.ValidateBodyAgainstHeader(block.Header, rawBody, out string? error), Is.False);
        Assert.That(error, Is.Not.Null);
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
            "max fee per blob gas less than block blob gas fee")
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
                .WithBlockAccessList(new ReadOnlyBlockAccessList())
                .WithEncodedBlockAccessList(Rlp.Encode(new ReadOnlyBlockAccessList()).Bytes).TestObject,
            parent,
            new CustomSpecProvider(((ForkActivation)0, Amsterdam.Instance)),
            "InvalidBlockLevelAccessListHash")
        { TestName = "InvalidBlockLevelAccessListHash" };

        yield return new TestCaseData(
            Build.A.Block
                .WithParent(parent)
                .WithBlobGasUsed(0)
                .WithWithdrawals([])
                .WithBlockAccessList(new ReadOnlyBlockAccessList())
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
                .WithBlockAccessList(new ReadOnlyBlockAccessList()).TestObject,
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

    // WithPrecompileChanges yields 25 BAL items; the cap is itemCount <= gasLimit / Eip7928Constants.ItemCost.
    [TestCase(50_000ul, true)]
    [TestCase(49_999ul, false)]
    public void ValidateSuggestedBlock_enforces_bal_item_gas_limit_boundary(ulong gasLimit, bool expectedValid)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithPrecompileChanges(parent.Hash!, timestamp: 12).TestObject;
        Block suggestedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(gasLimit)
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBal(bal)
            .TestObject;
        BlockValidator sut = AmsterdamSut(Always.Valid);

        bool isValid = sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        AssertValidation(expectedValid, isValid, error, "BlockAccessListGasLimitExceeded");
    }

    [TestCase(0u, true)]
    [TestCase(1u, true)]
    [TestCase(2u, true)]
    [TestCase(3u, true)]
    [TestCase(4u, false)]
    public void ValidateSuggestedBlock_enforces_bal_index_bounds(uint index, bool expectedValid)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        ReadOnlyAccountChanges accountChanges = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(index, 1))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(accountChanges)
            .TestObject;
        Block suggestedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(10_000ul)
            .WithTransactions(2, Amsterdam.Instance)
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBal(bal)
            .TestObject;
        BlockValidator sut = AmsterdamSut(Always.Valid);

        bool isValid = sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        AssertValidation(expectedValid, isValid, error, "InvalidBlockLevelAccessList");
    }

    [Test]
    public void ValidateSuggestedBlock_rejects_bal_item_when_gas_limit_allows_no_items()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        ReadOnlyAccountChanges accountChanges = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 1))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(accountChanges)
            .TestObject;
        Block suggestedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(0ul)
            .WithTransactions([])
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBal(bal)
            .TestObject;
        BlockValidator sut = AmsterdamSut();

        bool isValid = sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        AssertValidation(false, isValid, error, "BlockAccessListGasLimitExceeded");
    }

    [TestCase(30_000ul, true)]
    [TestCase(29_999ul, false)]
    public void ValidateProcessedBlock_enforces_bal_item_gas_limit_boundary_for_rlp_imported_blocks(ulong gasLimit, bool expectedValid)
    {
        // Hive eels/consume-rlp feeds blocks via RLP, which leaves Block.BlockAccessList null
        // (BlockDecoder does not decode BAL). The pre-execution check in
        // ValidateBlockLevelAccessList is gated on a non-null BAL and therefore skipped, so
        // ValidateProcessedBlock must catch the floor against the BAL produced during execution.
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithPrecompileChanges(parent.Hash!, timestamp: 12).TestObject;
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
            .WithBlockAccessListHash(balHash)
            .TestObject;
        // Build a GeneratedBlockAccessList with the same shape as the suggested BAL so the
        // ItemCount-based gas-limit boundary check exercises the generated path identically.
        GeneratedBlockAccessList generated = new();
        BlockAccessListAtIndex contribution = new();
        contribution.AddStorageChange(Eip2935Constants.BlockHashHistoryAddress, 0, 0, new UInt256(parent.Hash!.BytesToArray(), isBigEndian: true));
        UInt256 eip4788Slot1 = 12u % Eip4788Constants.RingBufferSize;
        UInt256 eip4788Slot2 = (12u % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize;
        contribution.AddStorageChange(Eip4788Constants.BeaconRootsAddress, eip4788Slot1, 0, 12);
        contribution.AddStorageRead(Eip4788Constants.BeaconRootsAddress, eip4788Slot2);
        for (UInt256 i = 0; i < 4; i++)
        {
            contribution.AddStorageRead(Eip7002Constants.WithdrawalRequestPredeployAddress, i);
            contribution.AddStorageRead(Eip7251Constants.ConsolidationRequestPredeployAddress, i);
        }
        generated.Merge(contribution);
        processedBlock.GeneratedBlockAccessList = generated;

        BlockValidator sut = AmsterdamSut();

        bool isValid = sut.ValidateProcessedBlock(processedBlock, [], suggestedBlock, out string? error);

        AssertValidation(expectedValid, isValid, error, "BlockAccessListGasLimitExceeded");
    }

    // EIP-7928 BlockAccessIndex must be in [0, txCount + 1]: 0 = pre-execution,
    // 1..n = transaction indices, n+1 = post-execution.
    // For a block with 0 transactions, valid indices are 0 and 1.
    [TestCase(0u, true)]
    [TestCase(1u, true)]
    [TestCase(2u, false)]
    [TestCase(uint.MaxValue - 1u, false)]
    public void ValidateSuggestedBlock_rejects_bal_index_above_tx_count_plus_one(uint balanceChangeIndex, bool expectedValid)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithPrecompileChanges(parent.Hash!, timestamp: 12)
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges([new BalanceChange(balanceChangeIndex, 100)])
                    .TestObject)
            .TestObject;

        Block suggestedBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(30_000_000ul)
            .WithBlobGasUsed(0)
            .WithWithdrawals([])
            .WithBal(bal)
            .TestObject;
        BlockValidator sut = AmsterdamSut();

        bool isValid = sut.ValidateSuggestedBlock(suggestedBlock, parent, out string? error);

        Assert.That(isValid, Is.EqualTo(expectedValid));
        if (!expectedValid)
        {
            Assert.That(error, Does.StartWith("InvalidBlockLevelAccessList"));
        }
    }

    private static BlockValidator AmsterdamSut(ITxValidator? tx = null) =>
        new(tx ?? new TxValidator(TestBlockchainIds.ChainId), Always.Valid, Always.Valid,
            new CustomSpecProvider(((ForkActivation)0, Amsterdam.Instance)), LimboLogs.Instance);

    private static void AssertValidation(bool expected, bool actual, string? error, string failPrefix)
    {
        Assert.That(actual, Is.EqualTo(expected));
        if (expected)
        {
            Assert.That(error, Is.Null);
        }
        else
        {
            Assert.That(error, Does.StartWith(failPrefix));
        }
    }
}

file static class BalBuilderExtensions
{
    public static BlockBuilder WithBal(this BlockBuilder builder, ReadOnlyBlockAccessList bal)
    {
        byte[] encoded = Rlp.Encode(bal).Bytes;
        return builder
            .WithBlockAccessList(bal)
            .WithEncodedBlockAccessList(encoded)
            .WithBlockAccessListHash(new Hash256(ValueKeccak.Compute(encoded).Bytes));
    }
}
