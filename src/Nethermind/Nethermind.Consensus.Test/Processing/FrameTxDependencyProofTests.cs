// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>The EIP-8141 block <c>dependency_proof</c>: its encoding, the dependencies it answers, and the checks a block
/// is held to before its transactions run.</summary>
public class FrameTxDependencyProofTests
{
    [Test]
    public void Encode_NoWitnesses_IsTheEmptyString() => Assert.That(FrameTxDependencyProof.Encode([]), Is.Empty);

    [Test]
    public void Encode_IsTheRawVersionByteThenTheWitnessList()
    {
        byte[][] witnesses = [[1, 2, 3], [], [0xff]];

        byte[] proof = FrameTxDependencyProof.Encode(witnesses);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof[0], Is.EqualTo(Eip8141Constants.DependencyProofVersionRaw));
            // [0x010203, 0x, 0xff] as an RLP list: 0x83 010203, 0x80, 0x81 ff.
            Assert.That(proof[1..], Is.EqualTo(new byte[] { 0xc7, 0x83, 0x01, 0x02, 0x03, 0x80, 0x81, 0xff }));
            Assert.That(FrameTxDependencyProof.TryDecode(proof, out byte[][] decoded), Is.True);
            Assert.That(decoded, Is.EqualTo(witnesses));
        }
    }

    [TestCase(new byte[] { 0x01, 0xc0 }, TestName = "TryDecode_AnUnknownVersion_IsRefused")]
    [TestCase(new byte[] { 0x00, 0xc1, 0x80, 0x80 }, TestName = "TryDecode_BytesAfterTheList_AreRefused")]
    [TestCase(new byte[] { 0x00, 0xc2, 0x80 }, TestName = "TryDecode_ATruncatedList_IsRefused")]
    public void TryDecode_Malformed_IsRefused(byte[] proof) =>
        Assert.That(FrameTxDependencyProof.TryDecode(proof, out _), Is.False);

    /// <summary>Claims over the same explicit digest share a dependency, however many transactions declare them, while
    /// claims over the canonical signature hash are each over their own transaction.</summary>
    [Test]
    public void BlockDependencies_AreSortedAndDeduplicated()
    {
        (TxFrameClaim digestClaim, _) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyB, 1);
        Transaction first = FrameTxWith(TestItem.AddressA, digestClaim, FrameTxTestFrames.Secp256k1Claim(null));
        Transaction second = FrameTxWith(TestItem.AddressB, digestClaim, FrameTxTestFrames.Secp256k1Claim(null));
        Block block = Build.A.Block.WithTransactions(first, second).TestObject;

        FrameTxDependencyProof.Dependency[] dependencies = FrameTxDependencyProof.BlockDependencies(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependencies, Has.Length.EqualTo(3));
            Assert.That(dependencies, Is.Ordered);
            Assert.That(dependencies, Is.Unique);
        }
    }

    [Test]
    public void AssignWitnesses_HandsEachClaimTheWitnessOfItsDependency()
    {
        (TxFrameClaim digestClaim, byte[] digestWitness) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyB, 1);
        Transaction first = FrameTxWith(TestItem.AddressA, digestClaim, FrameTxTestFrames.Secp256k1Claim(null));
        first.ClaimWitnesses = [digestWitness, Filled(65, 0x11)];
        Transaction second = FrameTxWith(TestItem.AddressB, digestClaim);
        second.ClaimWitnesses = [digestWitness];
        Transaction claimless = FrameTxWith(TestItem.AddressC);
        Block produced = Build.A.Block.WithTransactions(first, second, claimless).TestObject;
        FrameTxDependencyProof.Build(produced);
        Block received = ReceivedCopy(produced);

        FrameTxDependencyProof.AssignWitnesses(received);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received.Transactions[0].ClaimWitnesses, Is.EqualTo(first.ClaimWitnesses));
            Assert.That(received.Transactions[1].ClaimWitnesses, Is.EqualTo(second.ClaimWitnesses));
            Assert.That(received.Transactions[2].ClaimWitnesses, Is.Empty, "a frame transaction without claims gets an empty list, not none");
        }
    }

    [Test]
    public void Build_CarriesOneWitnessPerDistinctDependencyAndCommitsTheHeaderToIt()
    {
        (TxFrameClaim digestClaim, byte[] digestWitness) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyB, 1);
        Transaction first = FrameTxWith(TestItem.AddressA, digestClaim);
        first.ClaimWitnesses = [digestWitness];
        Transaction second = FrameTxWith(TestItem.AddressB, digestClaim);
        second.ClaimWitnesses = [digestWitness];
        Block block = Build.A.Block.WithTransactions(first, second).TestObject;

        FrameTxDependencyProof.Build(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.DependencyProof, Is.EqualTo(FrameTxDependencyProof.Encode([digestWitness])));
            Assert.That(block.Header.DependencyProofHash, Is.EqualTo(Keccak.Compute(block.DependencyProof!)));
        }
    }

    [Test]
    public void Build_BlockWithoutDependencies_CommitsToTheEmptyProof()
    {
        Block block = Build.A.Block.WithTransactions(FrameTxWith(TestItem.AddressA)).TestObject;

        FrameTxDependencyProof.Build(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.DependencyProof, Is.Empty);
            Assert.That(block.Header.DependencyProofHash, Is.EqualTo(Keccak.OfAnEmptyString));
        }
    }

    [Test]
    public void Build_TransactionWithoutWitnesses_IsRefused()
    {
        Block block = Build.A.Block.WithTransactions(FrameTxWith(TestItem.AddressA, FrameTxTestFrames.Secp256k1Claim(null))).TestObject;

        Assert.That(() => FrameTxDependencyProof.Build(block),
            Throws.InstanceOf<InvalidBlockException>().With.Message.Contains(FrameTxDependencyProof.MissingWitness));
    }

    public enum ProofDefect { HashMismatch, TooLarge, WitnessMissing, WitnessExtra, WitnessLength, NonEmptyWithoutDependencies, Malformed }

    [TestCase(ProofDefect.HashMismatch, FrameTxDependencyProof.HashMismatch)]
    [TestCase(ProofDefect.TooLarge, FrameTxDependencyProof.TooLarge)]
    [TestCase(ProofDefect.WitnessMissing, FrameTxDependencyProof.WitnessCountMismatch)]
    [TestCase(ProofDefect.WitnessExtra, FrameTxDependencyProof.WitnessCountMismatch)]
    [TestCase(ProofDefect.WitnessLength, FrameTxDependencyProof.WitnessLengthMismatch)]
    [TestCase(ProofDefect.NonEmptyWithoutDependencies, FrameTxDependencyProof.Malformed)]
    [TestCase(ProofDefect.Malformed, FrameTxDependencyProof.Malformed)]
    public void AssignWitnesses_ProofThatDoesNotFit_InvalidatesTheBlock(ProofDefect defect, string expectedError)
    {
        Transaction tx = defect == ProofDefect.NonEmptyWithoutDependencies
            ? FrameTxWith(TestItem.AddressA)
            : FrameTxWith(TestItem.AddressA, FrameTxTestFrames.Secp256k1Claim(null));
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        byte[] proof = defect switch
        {
            ProofDefect.TooLarge => new byte[Eip8141Constants.MaxDependencyProofBytes + 1],
            ProofDefect.WitnessMissing => [Eip8141Constants.DependencyProofVersionRaw, 0xc0],
            ProofDefect.WitnessExtra => FrameTxDependencyProof.Encode([new byte[65], new byte[65]]),
            ProofDefect.WitnessLength => FrameTxDependencyProof.Encode([new byte[64]]),
            ProofDefect.NonEmptyWithoutDependencies => [Eip8141Constants.DependencyProofVersionRaw, 0xc0],
            ProofDefect.Malformed => [Eip8141Constants.DependencyProofVersionRaw, 0xc2, 0x80],
            _ => FrameTxDependencyProof.Encode([new byte[65]]),
        };
        block.DependencyProof = proof;
        block.Header.DependencyProofHash = defect switch
        {
            ProofDefect.HashMismatch => TestItem.KeccakA,
            _ => Keccak.Compute(proof),
        };

        Assert.That(() => FrameTxDependencyProof.AssignWitnesses(block),
            Throws.InstanceOf<InvalidBlockException>().With.Message.Contains(expectedError));
    }

    /// <summary>The proof is not stored, so a block read back from the database has none: no verdict, not an invalid block.</summary>
    [Test]
    public void AssignWitnesses_UnavailableProof_ReachesNoVerdict()
    {
        Block block = Build.A.Block.WithTransactions(FrameTxWith(TestItem.AddressA, FrameTxTestFrames.Secp256k1Claim(null))).TestObject;
        block.Header.DependencyProofHash = Keccak.Compute(FrameTxDependencyProof.Encode([new byte[65]]));

        Assert.That(() => FrameTxDependencyProof.AssignWitnesses(block),
            Throws.Exception.Not.InstanceOf<InvalidBlockException>().With.Message.Contains(FrameTxDependencyProof.Unavailable));
    }

    public enum VerifierState { Code, NoCode, Delegated, Precompile }

    [TestCase(VerifierState.Code, true)]
    [TestCase(VerifierState.Precompile, true)]
    [TestCase(VerifierState.NoCode, false)]
    [TestCase(VerifierState.Delegated, false)]
    public void CheckVerifiersDeployed_CustomVerifierMustHaveCodeInThePreState(VerifierState verifierState, bool expectedValid)
    {
        Address verifier = verifierState == VerifierState.Precompile ? new Address("0x0000000000000000000000000000000000000002") : TestItem.AddressF;
        TxFrameClaim claim = new(verifier, default, default, gasLimit: 10_000, witnessLength: 1);
        Block block = Build.A.Block.WithTransactions(FrameTxWith(TestItem.AddressA, FrameTxTestFrames.Secp256k1Claim(null), claim)).TestObject;
        IReadOnlyStateProvider preState = Substitute.For<IReadOnlyStateProvider>();
        preState.IsContract(verifier).Returns(verifierState is VerifierState.Code or VerifierState.Delegated);
        preState.IsDelegatedCode(verifier).Returns(verifierState == VerifierState.Delegated);

        Assert.That(() => FrameTxDependencyProof.CheckVerifiersDeployed(block, preState, Prague.Instance), expectedValid
            ? Throws.Nothing
            : Throws.InstanceOf<InvalidBlockException>().With.Message.Contains(FrameTxDependencyProof.VerifierNotDeployed));
    }

    [Test]
    public void Budget_ChargesEachDistinctDependencyOnce()
    {
        (TxFrameClaim digestClaim, byte[] digestWitness) = FrameTxTestFrames.DigestClaim(TestItem.PrivateKeyB, 1);
        Transaction first = FrameTxWith(TestItem.AddressA, digestClaim);
        first.ClaimWitnesses = [digestWitness];
        Transaction second = FrameTxWith(TestItem.AddressB, digestClaim);
        second.ClaimWitnesses = [digestWitness];
        FrameTxDependencyProof.Budget budget = new();

        Assert.That(budget.TryFit(first, out FrameTxDependencyProof.Dependency[] added, out int addedLength, out _), Is.True);
        budget.Include(added, addedLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addedLength, Is.EqualTo(Rlp.LengthOf(digestWitness)));
            Assert.That(budget.TryFit(second, out added, out addedLength, out _), Is.True);
            Assert.That(added, Is.Empty);
            Assert.That(addedLength, Is.Zero);
        }
    }

    /// <summary>A transaction that would leave the proof unbuildable is skipped, so it cannot fail the block build.</summary>
    [TestCase(false, FrameTxDependencyProof.MissingWitness)]
    [TestCase(true, FrameTxDependencyProof.TooLarge)]
    public void Budget_RefusesATransactionThatWouldLeaveTheProofUnbuildable(bool overCap, string expectedError)
    {
        // Eight maximal witnesses and their list overhead exceed MAX_DEPENDENCY_PROOF_BYTES.
        const int ClaimCount = Eip8141Constants.MaxDependencyProofBytes / Eip8141Constants.MaxClaimWitnessBytes;
        TxFrameClaim[] claims = new TxFrameClaim[ClaimCount];
        byte[][] witnesses = new byte[overCap ? ClaimCount : ClaimCount - 1][];
        for (int i = 0; i < ClaimCount; i++)
        {
            byte[] msg = new byte[Hash256.Size];
            msg[^1] = (byte)(i + 1);
            claims[i] = new TxFrameClaim(TestItem.AddressF, default, msg, gasLimit: 1, witnessLength: Eip8141Constants.MaxClaimWitnessBytes);
            if (i < witnesses.Length) witnesses[i] = new byte[Eip8141Constants.MaxClaimWitnessBytes];
        }

        Transaction tx = FrameTxWith(TestItem.AddressA, claims);
        tx.ClaimWitnesses = witnesses;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new FrameTxDependencyProof.Budget().TryFit(tx, out _, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
        }
    }

    [Test]
    public void AssignWitnesses_BlockWithoutDependenciesNeedsNoProofToBeCarried()
    {
        Block block = Build.A.Block.WithTransactions(FrameTxWith(TestItem.AddressA)).TestObject;
        block.Header.DependencyProofHash = Keccak.OfAnEmptyString;

        Assert.That(() => FrameTxDependencyProof.AssignWitnesses(block), Throws.Nothing);
    }

    [Test]
    public void AssignWitnesses_HeaderWithoutTheCommitment_IsLeftAlone()
    {
        Block block = Build.A.Block.WithTransactions(FrameTxWith(TestItem.AddressA, FrameTxTestFrames.Secp256k1Claim(null))).TestObject;

        FrameTxDependencyProof.AssignWitnesses(block);

        Assert.That(block.Transactions[0].ClaimWitnesses, Is.Null);
    }

    private static Transaction FrameTxWith(Address sender, params TxFrameClaim[] claims)
    {
        Transaction tx = FrameTxTestFrames.FrameTx(sender, claims, FrameTxTestFrames.SelfVerify());
        tx.ChainId = TestBlockchainIds.ChainId;
        return tx;
    }

    /// <summary>The block as a validating node receives it: the same bodies, without the producer's witnesses.</summary>
    private static Block ReceivedCopy(Block produced)
    {
        Transaction[] transactions = new Transaction[produced.Transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = new Transaction();
            produced.Transactions[i].CopyTo(transactions[i]);
            transactions[i].ClaimWitnesses = null;
        }

        Block received = Build.A.Block.WithTransactions(transactions).TestObject;
        received.Header.DependencyProofHash = produced.Header.DependencyProofHash;
        received.DependencyProof = produced.DependencyProof;
        return received;
    }

    private static byte[] Filled(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
