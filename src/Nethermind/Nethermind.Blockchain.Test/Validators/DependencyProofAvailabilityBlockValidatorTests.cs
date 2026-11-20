// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
public class DependencyProofAvailabilityBlockValidatorTests
{
    private static readonly byte[] Proof = [1, 2, 3];

    // A block read back from the store, as one inserted by newPayload while syncing is, is validated with its stored proof.
    [Test]
    public void Validates_a_block_with_the_proof_the_store_holds()
    {
        (Block block, StarkProofStore store) = Committed(10);
        store.Insert(block.Number, block.Hash!, Proof);
        RecordingValidator inner = new();

        bool valid = Create(inner, store, finalized: null).ValidateSuggestedBlock(block, Build.A.BlockHeader.TestObject, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.True, error);
            Assert.That(inner.ValidatedProof, Is.EqualTo(Proof));
            Assert.That(block.StarkProof, Is.Null, "the caller's block, which may be a cached instance, does not gain the proof");
        }
    }

    // EIPs#12549 and EIP-8498: a missing proof is accepted only at or below the finalized block, and only that failure is.
    [TestCase(null, null, false)]
    [TestCase(9UL, BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase(10UL, BlockErrorMessages.MissingDependencyProof, true)]
    [TestCase(11UL, BlockErrorMessages.MissingDependencyProof, true)]
    [TestCase(11UL, BlockErrorMessages.InvalidRecursiveStark, false)]
    public void Trusts_finality_only_for_a_missing_proof(ulong? finalizedNumber, string? innerError, bool expected)
    {
        (Block block, StarkProofStore store) = Committed(10);
        RecordingValidator inner = new() { Error = innerError ?? BlockErrorMessages.MissingDependencyProof };

        bool valid = Create(inner, store, finalizedNumber).ValidateSuggestedBlock(block, Build.A.BlockHeader.TestObject, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected));
            Assert.That(error, expected ? Is.Null : Is.EqualTo(inner.Error));
        }
    }

    private static DependencyProofAvailabilityBlockValidator Create(IBlockValidator inner, IStarkProofStore store, ulong? finalized)
    {
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.FindFinalizedHeader().Returns(finalized is { } number ? Build.A.BlockHeader.WithNumber(number).TestObject : null);
        return new DependencyProofAvailabilityBlockValidator(inner, store, finder);
    }

    private static (Block Block, StarkProofStore Store) Committed(ulong number)
    {
        Block block = Build.A.Block.WithNumber(number).TestObject;
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(Proof), Keccak.Compute("deps"));
        block.Header.Hash = block.Header.CalculateHash();
        return (block, new StarkProofStore(new TestMemDb()));
    }

    /// <summary>Fails with <see cref="Error"/> unless the block carries a proof, recording the proof it was given.</summary>
    private sealed class RecordingValidator : IBlockValidator
    {
        public string Error { get; init; } = BlockErrorMessages.MissingDependencyProof;
        public byte[]? ValidatedProof { get; private set; }

        public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true)
        {
            ValidatedProof = block.StarkProof;
            error = block.StarkProof is null ? Error : null;
            return error is null;
        }

        public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) => ValidateSuggestedBlock(block, null!, out error);
        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) { error = null; return true; }
        public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool ValidateWithdrawals(Block block, out string? error) { error = null; return true; }
    }
}
