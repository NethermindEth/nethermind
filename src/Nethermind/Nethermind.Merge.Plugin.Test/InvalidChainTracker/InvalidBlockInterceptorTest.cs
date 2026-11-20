// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
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
    private StarkProofStore _proofStore = null!;

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
        _proofStore = new StarkProofStore(new TestMemDb());
        _invalidBlockInterceptor = new(
            _baseValidator,
            _tracker,
            _proofStore,
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
        BlockValidator bodyValidator = new(Always.Valid, Always.Valid, Always.Valid, MainnetSpecProvider.Instance, NullLogManager.Instance);
        _baseValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>())
            .Returns(call => bodyValidator.ValidateBodyAgainstHeader(call.Arg<BlockHeader>(), call.Arg<BlockBody>(), out _));
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        byte[] authenticProof = [];
        block.Header.RecursiveStark = new(Keccak.Compute(authenticProof), Keccak.OfAnEmptyString);
        block.Header.Hash = block.Header.CalculateHash();
        block.StarkProof = missing ? null : [2];
        _baseValidator.ValidateSuggestedBlock(block, parent, out _).Returns(false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HeaderValidator.ValidateHash(block.Header), Is.True);
        }

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

    // EIP-8288: a block processed from the block store lacks the proof kept apart from it. Its invalidity is tracked
    // when the stored proof matches, and a stored instance, which the block store may cache, never gains the proof.
    [Test]
    public void Processed_stored_block_is_tracked_with_its_stored_proof_without_attaching_it([Values] bool proofStored)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        byte[] proof = [1, 2, 3];
        block.Header.RecursiveStark = new(Keccak.Compute(proof), Keccak.Compute("deps"));
        block.Header.Hash = block.Header.CalculateHash();
        block.StarkProof = proof;
        IBlockStore blockStore = new StarkProofSplittingBlockStore(new BlockStore(new TestMemDb()), _proofStore);
        blockStore.Insert(block);
        blockStore.Cache(block);
        if (!proofStored) _proofStore.Delete(block.Number, block.Hash!);
        Block stored = blockStore.Get(block.Number, block.Hash!)!;
        _baseValidator.ValidateProcessedBlock(stored, Arg.Any<TxReceipt[]>(), stored, out _).Returns(false);
        _baseValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out _)
            .Returns(static call => BlockValidator.StarkProofMatchesCommitment(call.Arg<BlockHeader>(), call.Arg<BlockBody>()));

        _invalidBlockInterceptor.ValidateProcessedBlock(stored, [], stored, out _);

        using (Assert.EnterMultipleScope())
        {
            _tracker.Received(proofStored ? 1 : 0).OnInvalidBlock(block.Hash!, block.ParentHash);
            Assert.That(stored.StarkProof, Is.Null);
            Assert.That(blockStore.Get(block.Number, block.Hash!)!.StarkProof, Is.Null);
        }
    }
}
