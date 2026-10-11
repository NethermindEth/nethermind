// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class InvalidBlockInterceptorTest
{
    private IBlockValidator _baseValidator = null!;
#pragma warning disable NUnit1032
    private IInvalidChainTracker _tracker = null!;
#pragma warning restore NUnit1032
    private InvalidBlockInterceptor _invalidBlockInterceptor = null!;

    [SetUp]
    public void Setup()
    {
        _baseValidator = Substitute.For<IBlockValidator>();
        _baseValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out _)
            .Returns(f =>
            {
                BlockHeader blockHeader = f.Arg<BlockHeader>();
                BlockBody blockBody = f.Arg<BlockBody>();
                return BlockValidator.ValidateTxRootMatchesTxs(blockHeader, blockBody, out _) &&
                       BlockValidator.ValidateUnclesHashMatches(blockHeader, blockBody, out _) &&
                       BlockValidator.ValidateWithdrawalsHashMatches(blockHeader, blockBody, out _);
            });
        _tracker = Substitute.For<IInvalidChainTracker>();
        _invalidBlockInterceptor = new(
            _baseValidator,
            _tracker,
            NullLogManager.Instance);
    }

    [TearDown]
    public void TearDown() => (_invalidBlockInterceptor as IDisposable)?.Dispose();

    [Test]
    public void Prepared_transactions_preserve_invalid_chain_tracking([Values] bool valid)
    {
        Assume.That(Core.Cpu.RuntimeInformation.IsSingleProcessor, Is.False);
        ITxValidator txValidator = Substitute.For<ITxValidator>();
        txValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>(), Arg.Any<ulong>())
            .Returns(valid ? ValidationResult.Success : new ValidationResult("invalid transaction"));
        BlockValidator validator = new(txValidator, Always.Valid, Always.Valid,
            new TestSingleReleaseSpecProvider(Byzantium.Instance), LimboLogs.Instance);
        InvalidBlockInterceptor interceptor = new(validator, _tracker, LimboLogs.Instance);
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent)
            .WithTransactions(Enumerable.Repeat(Build.A.Transaction.SignedAndResolved().TestObject, 32).ToArray()).TestObject;
        using BlockValidator.TransactionValidation? prepared = interceptor.PrepareTransactions(block);
        Assert.That(prepared, Is.Not.Null);

        Assert.That(interceptor.ValidateSuggestedBlock(block, parent, out _, validateHashes: false, prepared), Is.EqualTo(valid));

        _tracker.Received().SetChildParent(block.Hash!, block.ParentHash!);
        _tracker.Received(valid ? 0 : 1).OnInvalidBlock(block.Hash!, block.ParentHash);
        txValidator.Received(valid ? 32 : 1).IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>(), Arg.Any<ulong>());
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateSuggestedBlock(bool baseReturnValue, bool isInvalidBlockReported)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(baseReturnValue);
        _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);

        _tracker.Received().SetChildParent(block.GetOrCalculateHash(), block.ParentHash!);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(block.GetOrCalculateHash(), block.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(block.GetOrCalculateHash(), block.ParentHash);
        }
    }

    /// <summary>
    /// The interceptor is the decorator registered for <see cref="IBlockValidator"/>, so a missing overload here
    /// would route every opt-out to the interface default and silently validate fully.
    /// </summary>
    [Test]
    public void TestValidateHeaderForwardsValidateHash([Values] bool validateHash)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
        _baseValidator.Validate(header, parent, false, out Arg.Any<string?>(), validateHash).Returns(true);

        Assert.That(_invalidBlockInterceptor.Validate(header, parent, false, out _, validateHash), Is.True);

        _baseValidator.Received().Validate(header, parent, false, out Arg.Any<string?>(), validateHash);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateProcessedBlock(bool baseReturnValue, bool isInvalidBlockReported)
    {
        Block block = Build.A.Block.TestObject;
        Block suggestedBlock = Build.A.Block.WithExtraData(new byte[] { 1 }).TestObject;
        TxReceipt[] txs = [];
        _baseValidator.ValidateProcessedBlock(block, txs, suggestedBlock, out _).Returns(baseReturnValue);
        _invalidBlockInterceptor.ValidateProcessedBlock(block, txs, suggestedBlock);

        _tracker.Received().SetChildParent(suggestedBlock.GetOrCalculateHash(), suggestedBlock.ParentHash!);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(suggestedBlock.GetOrCalculateHash(), suggestedBlock.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(suggestedBlock.GetOrCalculateHash(), suggestedBlock.ParentHash);
        }
    }

    [Test]
    public void TestInvalidBlockhashShouldNotGetTracked()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        block.Header.StateRoot = Keccak.Zero;

        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(false);
        _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);

        _tracker.DidNotReceive().SetChildParent(block.GetOrCalculateHash(), block.ParentHash!);
        _tracker.DidNotReceive().OnInvalidBlock(block.GetOrCalculateHash(), block.ParentHash);
    }

    [Test]
    public void TestBlockWithNotMatchingTxShouldNotGetTracked()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block
            .WithParent(parent)
            .WithTransactions(10, MainnetSpecProvider.Instance)
            .TestObject;

        block = new Block(block.Header, block.Body.WithChangedTransactions(
            block.Transactions.Take(9).ToArray()
        ));

        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(false);
        _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);

        _tracker.DidNotReceive().SetChildParent(block.GetOrCalculateHash(), block.ParentHash!);
        _tracker.DidNotReceive().OnInvalidBlock(block.GetOrCalculateHash(), block.ParentHash);
    }

    [Test]
    public void TestBlockWithIncorrectWithdrawalsShouldNotGetTracked()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block
            .WithParent(parent)
            .WithWithdrawals(10)
            .TestObject;

        block = new Block(block.Header, block.Body.WithChangedWithdrawals(
            block.Withdrawals!.Take(8).ToArray()
        ));

        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(false);
        _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);

        _tracker.DidNotReceive().SetChildParent(block.GetOrCalculateHash(), block.ParentHash!);
        _tracker.DidNotReceive().OnInvalidBlock(block.GetOrCalculateHash(), block.ParentHash);
    }

}
