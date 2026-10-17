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

    [Test]
    public void Corrupt_peer_proof_does_not_poison_valid_header([Values] bool missing)
    {
        BlockValidator bodyValidator = new(Always.Valid, Always.Valid, Always.Valid, MainnetSpecProvider.Instance,
            NullLogManager.Instance, Substitute.For<ILeanProofVerifier>());
        _baseValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>())
            .Returns(call => bodyValidator.ValidateBodyAgainstHeader(call.Arg<BlockHeader>(), call.Arg<BlockBody>(), out _));
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        byte[] authenticProof = [];
        block.Header.RecursiveStark = new(Keccak.Compute(authenticProof), Keccak.OfAnEmptyString);
        block.Header.Hash = block.Header.CalculateHash();
        block.StarkProof = missing ? null : [2];
        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(false);
        Assert.That(HeaderValidator.ValidateHash(block.Header), Is.True);

        Assert.That(_invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _), Is.False);

        using (Assert.EnterMultipleScope())
        {
            _tracker.DidNotReceive().OnInvalidBlock(block.Hash!, block.ParentHash);
            _tracker.DidNotReceive().SetChildParent(block.Hash!, block.ParentHash!);
        }
        block.StarkProof = authenticProof;
        Assert.That(_invalidBlockInterceptor.ValidateSuggestedBlock(block, parent, out _), Is.False);
        _tracker.Received().OnInvalidBlock(block.Hash!, block.ParentHash);
    }

}
