// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Eip8288;

public class FocilInclusionListTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);

    private static Transaction DepTx(byte scheme)
    {
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = scheme;
        return new Transaction
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, data)],
        };
    }

    [Test]
    public void Request_memo_reuses_only_identical_proof_and_public_inputs()
    {
        FakeLeanProofVerifier backend = new(true);
        InclusionListProofVerifier verifier = new(backend);
        ValueHash256 deps = ValueKeccak.Compute([1]);
        byte[] key = [3];
        byte[] proof = [1];
        for (int check = 0; check < 4; check++) Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(1));
        proof[0] = 2;
        Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(2));
        key[0] = 4;
        Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(3));
        deps = ValueKeccak.Compute([2]);
        Assert.That(verifier.VerifyRecursiveStark(in deps, key, proof), Is.True);
        Assert.That(backend.VerificationCalls, Is.EqualTo(4));
    }

    [Test]
    public void Empty_focil_round_trips()
    {
        FocilInclusionList focil = new()
        {
            Transactions = [],
            RecursiveStark = new RecursiveStark([1, 2, 3], Keccak.Compute("deps")),
        };

        Rlp rlp = FocilInclusionListDecoder.Instance.Encode(focil);
        RlpReader reader = new(rlp.Bytes);
        FocilInclusionList decoded = FocilInclusionListDecoder.Instance.Decode(ref reader)!;

        Assert.That(decoded.Transactions.Count, Is.EqualTo(0));
        Assert.That(decoded.RecursiveStark.StarkProof, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(decoded.RecursiveStark.BlockDepsHash, Is.EqualTo(Keccak.Compute("deps")));
    }

    [Test]
    public void Valid_when_recursive_stark_commits_to_all_dependencies()
    {
        List<Transaction> txs = [DepTx(Eip8288Constants.LeanSphincsScheme), DepTx(Eip8288Constants.LeanStarkScheme)];
        List<FrameDependency> deps = [];
        foreach (Transaction tx in txs) deps.AddRange(Eip8288Dependencies.ForTransaction(tx));

        FocilInclusionList focil = new()
        {
            Transactions = txs,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(FocilInclusionListValidator.Validate(focil, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Rejects_deps_hash_mismatch()
    {
        FocilInclusionList focil = new()
        {
            Transactions = [DepTx(Eip8288Constants.LeanSphincsScheme)],
            RecursiveStark = new RecursiveStark([1], Keccak.Compute("wrong")),
        };

        Assert.That(FocilInclusionListValidator.Validate(focil, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FocilInclusionListValidator.DepsHashMismatch));
    }
    [Test]
    public void Typed_transactions_use_canonical_inclusion_list_encoding()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        FocilInclusionList list = new() { Transactions = [tx], RecursiveStark = new([1], Keccak.Zero) };
        Rlp encoded = FocilInclusionListDecoder.Instance.Encode(list);
        RlpReader reader = new(encoded.Bytes);
        reader.ReadSequenceLength();
        reader.ReadSequenceLength();
        byte[] entry = reader.DecodeByteArray();
        Assert.That(entry[0], Is.EqualTo((byte)TxType.EIP1559));
        reader = new(encoded.Bytes);
        Assert.That(FocilInclusionListDecoder.Instance.Decode(ref reader)!.Transactions[0].Hash, Is.EqualTo(tx.Hash));
    }

    [Test]
    public void Rejects_null_transaction_entry()
    {
        Rlp encoded = Rlp.Encode(Rlp.Encode(new[] { Rlp.Encode(new byte[] { 0xc0 }) }),
            Rlp.Encode(new[] { Rlp.Encode(new byte[] { 1 }), Rlp.Encode(Keccak.Zero) }));
        Assert.Throws<RlpException>(() =>
        {
            RlpReader reader = new(encoded.Bytes);
            FocilInclusionListDecoder.Instance.Decode(ref reader);
        });
    }

    [TestCase(Eip8288Constants.MaxProofBytes, true)]
    [TestCase(Eip8288Constants.MaxProofBytes + 1, false)]
    public void Proof_decoder_matches_native_size_bound(int proofBytes, bool accepted)
    {
        FocilInclusionList list = new()
        {
            Transactions = [],
            RecursiveStark = new RecursiveStark(new byte[proofBytes], Keccak.Zero)
        };
        Rlp encoded = FocilInclusionListDecoder.Instance.Encode(list);
        if (accepted)
        {
            RlpReader reader = new(encoded.Bytes);
            Assert.That(FocilInclusionListDecoder.Instance.Decode(ref reader)!.RecursiveStark.StarkProof, Has.Length.EqualTo(proofBytes));
        }
        else
        {
            Assert.That(() =>
            {
                RlpReader reader = new(encoded.Bytes);
                FocilInclusionListDecoder.Instance.Decode(ref reader);
            }, Throws.InstanceOf<RlpException>());
        }
    }

}
