// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Specs;
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

    /// <summary>
    /// Every processing environment shares this validator, read-only ones included, so the outcome of an execution
    /// is only forwarded here; the node's own import reports its failures through the processing queue.
    /// </summary>
    [Test]
    public void TestValidateProcessedBlockOnlyForwards([Values] bool baseReturnValue)
    {
        Block block = Build.A.Block.TestObject;
        Block suggestedBlock = Build.A.Block.WithExtraData(new byte[] { 1 }).TestObject;
        TxReceipt[] txs = [];
        _baseValidator.ValidateProcessedBlock(block, txs, suggestedBlock, out _).Returns(baseReturnValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_invalidBlockInterceptor.ValidateProcessedBlock(block, txs, suggestedBlock), Is.EqualTo(baseReturnValue));
            Assert.That(_tracker.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public void TestValidationInUntrackedScopeIsNotTracked([Values] bool baseReturnValue)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(baseReturnValue);
        _baseValidator.Validate(block.Header, parent, false, out _).Returns(baseReturnValue);
        _baseValidator.ValidateWithdrawals(block, out _).Returns(baseReturnValue);

        using (UntrackedValidation.Begin())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _), Is.EqualTo(baseReturnValue));
            Assert.That(_invalidBlockInterceptor.Validate(block.Header, parent, false, out _), Is.EqualTo(baseReturnValue));
            Assert.That(_invalidBlockInterceptor.ValidateWithdrawals(block, out _), Is.EqualTo(baseReturnValue));
        }

        Assert.That(_tracker.ReceivedCalls(), Is.Empty);

        _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);
        _tracker.Received().SetChildParent(block.GetOrCalculateHash(), block.ParentHash!);
    }

    [Test]
    public void TestDefaultUntrackedScopeEndsNothing()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(true);

        default(UntrackedValidation.Scope).Dispose();
        using (UntrackedValidation.Begin())
        {
            _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);
        }

        Assert.That(_tracker.ReceivedCalls(), Is.Empty, "a default scope must not unbalance the depth");
    }

    [Test]
    public void TestUntrackedScopeDisposedTwiceEndsOnlyItself()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(true);

        UntrackedValidation.Scope scope = UntrackedValidation.Begin();
        scope.Dispose();
        scope.Dispose();
        using (UntrackedValidation.Begin())
        {
            _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);
        }

        Assert.That(_tracker.ReceivedCalls(), Is.Empty, "a scope disposed twice must not unbalance the depth");
        _invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _);
        _tracker.Received().SetChildParent(block.GetOrCalculateHash(), block.ParentHash!);
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
