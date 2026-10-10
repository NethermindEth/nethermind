// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class MempoolWrapperTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);

    private static FrameDependency Sphincs(string tag) =>
        new(Eip8288Constants.LeanSphincsScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    private static FrameDependency Stark(string tag) =>
        new(Eip8288Constants.LeanStarkScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    [Test]
    public void Direct_wrapper_round_trips()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = ByHash(Keccak.Compute("tx1"), Keccak.Compute("tx2")),
            Mode = MempoolWrapper.ModeDirect,
            Deps = [Sphincs("a"), Stark("b")],
            Proofs = [[1, 2], [3, 4]],
        };

        MempoolWrapper decoded = RoundTrip(wrapper);

        Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeDirect));
        Assert.That(decoded.Transactions.Count, Is.EqualTo(2));
        Assert.That(decoded.Transactions.All(t => t.IsHashOnly), Is.True);
        Assert.That(decoded.Deps, Is.EqualTo(wrapper.Deps));
        Assert.That(decoded.Proofs!.Count, Is.EqualTo(2));
        Assert.That(decoded.Proofs![1], Is.EqualTo(new byte[] { 3, 4 }));
    }

    [Test]
    public void Recursive_wrapper_round_trips()
    {
        List<FrameDependency> deps = [Sphincs("a")];
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([7, 8, 9], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        MempoolWrapper decoded = RoundTrip(wrapper);

        Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeRecursive));
        Assert.That(decoded.RecursiveStark!.StarkProof, Is.EqualTo(new byte[] { 7, 8, 9 }));
        Assert.That(decoded.RecursiveStark!.BlockDepsHash, Is.EqualTo(new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))));
    }

    [Test]
    public void Direct_wrapper_valid()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [Sphincs("a"), Stark("b")],
            Proofs = [[1], [2]],
        };

        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Recursive_wrapper_valid()
    {
        List<FrameDependency> deps = [Sphincs("a")];
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Direct_wrapper_rejects_proof_count_mismatch()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [Sphincs("a"), Stark("b")],
            Proofs = [[1]],
        };

        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.ProofCountMismatch));
    }

    [Test]
    public void Recursive_wrapper_rejects_deps_hash_mismatch()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = [Sphincs("a")],
            RecursiveStark = new RecursiveStark([1], Keccak.Compute("wrong")),
        };

        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsHashMismatch));
    }

    [Test]
    public void Wrapper_rejects_too_many_sig_deps()
    {
        List<FrameDependency> deps = Enumerable.Range(0, Eip8288Constants.MaxLeanSigDepsPerWrapper + 1).Select(i => Sphincs(i.ToString())).ToList();
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("transaction exceeds EIP-8288 dependency limits"));
    }

    [Test]
    public void Wrapper_rejects_too_many_stark_deps()
    {
        List<FrameDependency> deps = [Stark("a"), Stark("b")];
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("transaction exceeds EIP-8288 dependency limits"));
    }

    // A deps blob of 96k + r decoded to the same wrapper as the 96k one, so two wire encodings mapped
    // to one object.
    [Test]
    public void Deps_blob_that_is_not_a_multiple_of_the_triple_length_is_rejected()
    {
        Rlp encoded = Rlp.Encode(
            Rlp.Encode(new[] { Rlp.Encode(Keccak.Compute("tx1").BytesToArray()) }),
            Rlp.Encode((ulong)MempoolWrapper.ModeDirect),
            Rlp.Encode(new[]
            {
                Rlp.Encode(new byte[Eip8288Constants.DependencyTripleLength + 1]),
                Rlp.Encode(System.Array.Empty<Rlp>()),
            }));

        Assert.That(() =>
        {
            RlpReader reader = new(encoded.Bytes);
            MempoolWrapperDecoder.Instance.Decode(ref reader);
        }, Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Unknown_transaction_hash_is_rejected()
    {
        MempoolWrapper wrapper = new() { Transactions = [new WrapperTransaction(Keccak.Compute("missing"))], Mode = MempoolWrapper.ModeDirect, Deps = [], Proofs = [] };
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.UnknownTransaction));
    }

    [Test]
    public void Duplicate_dependencies_are_rejected()
    {
        FrameDependency dependency = Sphincs("a");
        MempoolWrapper wrapper = new() { Transactions = [new WrapperTransaction(Keccak.Compute("tx"))], Mode = MempoolWrapper.ModeDirect, Deps = [dependency, dependency], Proofs = [[1], [1]] };
        Assert.That(ValidateResolved(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsMismatch));
    }

    [Test]
    public void Recursive_wrapper_rejects_extra_inner_values()
    {
        Rlp encoded = Rlp.Encode(
            Rlp.Encode(new[] { HashEntry(Keccak.Compute("tx")) }),
            Rlp.Encode((ulong)MempoolWrapper.ModeRecursive),
            Rlp.Encode(new[] { Rlp.Encode(System.Array.Empty<Rlp>()), Rlp.Encode(new byte[] { 1 }), Rlp.Encode(Keccak.Compute("deps")) }));
        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Wrapper_rejects_noncanonical_scheme_word([Values] bool unknownScheme)
    {
        byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
        dependency[31] = unknownScheme ? (byte)0xff : Eip8288Constants.LeanSphincsScheme;
        if (!unknownScheme) dependency[0] = 1;
        Rlp encoded = Rlp.Encode(Rlp.Encode(new[] { HashEntry(Keccak.Compute("tx")) }), Rlp.Encode(0UL),
            Rlp.Encode(new[] { Rlp.Encode(new[] { Rlp.Encode(dependency) }), Rlp.Encode(new[] { Rlp.Encode(new byte[] { 1 }) }) }));
        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Encodes_the_eip8437_kind1_body()
    {
        Hash256 hash = Keccak.Compute("tx");
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(hash)],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = [Sphincs("a")],
            RecursiveStark = new RecursiveStark([7], new Hash256(Eip8288Dependencies.ComputeDepsHash([Sphincs("a")])))
        };
        // RLP([[[1, hash]], 1, [[dependency_bytes96], stark_proof]]): tagged entries, a list of triples, and only the proof.
        Rlp expected = Rlp.Encode(
            Rlp.Encode(new[] { HashEntry(hash) }),
            Rlp.Encode(1UL),
            Rlp.Encode(new[] { Rlp.Encode(new[] { Rlp.Encode(Eip8288Dependencies.Serialize([Sphincs("a")])) }), Rlp.Encode(new byte[] { 7 }) }));

        Assert.That(MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes, Is.EqualTo(expected.Bytes));
    }

    [TestCase("untagged")]
    [TestCase("unknown-tag")]
    [TestCase("short-hash")]
    [TestCase("unsorted")]
    [TestCase("duplicate")]
    [TestCase("empty")]
    [TestCase("short-dependency")]
    public void Decode_rejects_malformed_kind1_bodies(string scenario)
    {
        Hash256[] hashes = [.. ByHash(Keccak.Compute("a"), Keccak.Compute("b")).Select(static entry => entry.Hash!)];
        Rlp[] entries = scenario switch
        {
            "untagged" => [Rlp.Encode(hashes[0])],
            "unknown-tag" => [Rlp.Encode(Rlp.Encode(2UL), Rlp.Encode(hashes[0]))],
            "short-hash" => [Rlp.Encode(Rlp.Encode(1UL), Rlp.Encode(new byte[31]))],
            "unsorted" => [HashEntry(hashes[1]), HashEntry(hashes[0])],
            "duplicate" => [HashEntry(hashes[0]), HashEntry(hashes[0])],
            "empty" => [],
            _ => [HashEntry(hashes[0])]
        };
        Rlp deps = Rlp.Encode(scenario == "short-dependency" ? [Rlp.Encode(new byte[Eip8288Constants.DependencyTripleLength - 1])] : System.Array.Empty<Rlp>());
        Rlp encoded = Rlp.Encode(Rlp.Encode(entries), Rlp.Encode(0UL), Rlp.Encode(deps, Rlp.Encode(System.Array.Empty<Rlp>())));

        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    private static WrapperTransaction[] ByHash(params Hash256[] hashes) =>
        [.. hashes.OrderBy(static hash => hash.ToString()).Select(static hash => new WrapperTransaction(hash))];

    private static Rlp HashEntry(Hash256 hash) => Rlp.Encode(Rlp.Encode(1UL), Rlp.Encode(hash));

    private static MempoolWrapper Decode(Rlp encoded)
    {
        RlpReader reader = new(encoded.Bytes);
        return MempoolWrapperDecoder.Instance.Decode(ref reader)!;
    }

    private static bool ValidateResolved(MempoolWrapper wrapper, ILeanProofVerifier verifier, out string? error) =>
        MempoolWrapperValidator.Validate(wrapper, verifier, out error, _ => new Transaction
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, Eip8288Dependencies.Serialize(wrapper.Deps))]
        });

    [Test]
    public void Decode_rejects_excessive_transaction_entries()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = ByHash([.. Enumerable.Range(0, LeanProofStore.MaxWrapperTransactions + 1).Select(static index => Keccak.Compute(index.ToString()))]),
            Deps = [],
            Proofs = [],
            Mode = MempoolWrapper.ModeDirect
        };
        Assert.That(() => RoundTrip(wrapper), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Decode_rejects_excessive_witness_entries()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx"))],
            Deps = [],
            Proofs = Enumerable.Range(0, Eip8288Constants.MaxLeanSigDepsPerWrapper + Eip8288Constants.MaxLeanStarkDepsPerWrapper + 1)
                .Select(_ => (byte[])[]).ToArray(),
            Mode = MempoolWrapper.ModeDirect
        };
        Assert.That(() => RoundTrip(wrapper), Throws.InstanceOf<RlpException>());
    }

    private static Transaction DepTx(IReadOnlyList<FrameDependency> deps) => new()
    {
        Type = TxType.FrameTx,
        Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, Eip8288Dependencies.Serialize(deps))]
    };

    /// <summary>A wrapper over transactions declaring <paramref name="transactionDeps"/>, with a resolver for its hash entries.</summary>
    private static (MempoolWrapper Wrapper, Func<Hash256, Transaction?> Resolve) Batch(byte mode, IReadOnlyList<FrameDependency>[] transactionDeps)
    {
        Dictionary<Hash256, Transaction> transactions = [];
        for (int i = 0; i < transactionDeps.Length; i++) transactions[Keccak.Compute($"tx{i}")] = DepTx(transactionDeps[i]);
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(transactionDeps.SelectMany(static d => d));
        MempoolWrapper wrapper = new()
        {
            Transactions = ByHash([.. transactions.Keys]),
            Mode = mode,
            Deps = deps,
            Proofs = mode == MempoolWrapper.ModeDirect ? [.. deps.Select(static _ => new byte[] { 1 })] : null,
            RecursiveStark = mode == MempoolWrapper.ModeRecursive
                ? new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
                : null
        };
        return (wrapper, hash => transactions.GetValueOrDefault(hash));
    }

    private static FrameDependency[] Sphincses(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => Sphincs($"{prefix}{i}"))];

    [TestCase(MempoolWrapper.ModeRecursive, true)]
    [TestCase(MempoolWrapper.ModeDirect, false)]
    public void Two_full_direct_batches_combine_only_into_an_aggregate(byte mode, bool accepted)
    {
        (MempoolWrapper wrapper, Func<Hash256, Transaction?> resolve) = Batch(mode,
            [Sphincses("a", Eip8288Constants.MaxSigsPerTx), Sphincses("b", Eip8288Constants.MaxSigsPerTx)]);
        bool valid = MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error, resolve);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Deps, Has.Count.EqualTo(2 * Eip8288Constants.MaxSigsPerTx));
            Assert.That(valid, Is.EqualTo(accepted), error);
            if (!accepted) Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TooManySigDeps));
        }
    }

    [Test]
    public void Aggregation_does_not_admit_a_transaction_over_its_own_limits([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        (MempoolWrapper wrapper, Func<Hash256, Transaction?> resolve) = Batch(mode, [Sphincses("a", Eip8288Constants.MaxSigsPerTx + 1)]);
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error, resolve), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TransactionLimits));
    }

    [TestCase(Eip8288Constants.MaxDepsPerAggregate, null)]
    [TestCase(Eip8288Constants.MaxDepsPerAggregate + 1, MempoolWrapperValidator.TooManyAggregateDeps)]
    public void Aggregate_dependency_ceiling_is_checked_before_verification(int count, string? expected)
    {
        IReadOnlyList<FrameDependency>[] transactions = [.. Sphincses("d", count).Chunk(Eip8288Constants.MaxSigsPerTx)];
        (MempoolWrapper wrapper, Func<Hash256, Transaction?> resolve) = Batch(MempoolWrapper.ModeRecursive, transactions);
        FakeLeanProofVerifier verifier = new(true);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, out string? error, resolve);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected is null), error);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(expected is null ? 1 : 0));
        }
    }

    [TestCase(Eip8288Constants.MaxTxsPerWrapper, null)]
    [TestCase(Eip8288Constants.MaxTxsPerWrapper + 1, MempoolWrapperValidator.TooManyTransactions)]
    public void Aggregate_transaction_ceiling_holds_even_when_transactions_share_dependencies(int count, string? expected)
    {
        FrameDependency[] shared = [Sphincs("shared")];
        (MempoolWrapper wrapper, Func<Hash256, Transaction?> resolve) = Batch(MempoolWrapper.ModeRecursive,
            [.. Enumerable.Repeat<IReadOnlyList<FrameDependency>>(shared, count)]);
        bool valid = MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error, resolve);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Deps, Has.Count.EqualTo(1), "repeated declarations count once");
            Assert.That(valid, Is.EqualTo(expected is null), error);
            Assert.That(error, Is.EqualTo(expected));
        }
    }

    [TestCase(Eip8288Constants.MaxLeanStarkDepsPerAggregate, null)]
    [TestCase(Eip8288Constants.MaxLeanStarkDepsPerAggregate + 1, MempoolWrapperValidator.TooManyAggregateStarkDeps)]
    public void Aggregate_leanstark_ceiling_is_checked_before_verification(int count, string? expected)
    {
        (MempoolWrapper wrapper, Func<Hash256, Transaction?> resolve) = Batch(MempoolWrapper.ModeRecursive,
            [.. Enumerable.Range(0, count).Select(i => (IReadOnlyList<FrameDependency>)[Stark($"s{i}"), Sphincs($"p{i}")])]);
        FakeLeanProofVerifier verifier = new(true);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, out string? error, resolve);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected is null), error);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(expected is null ? 1 : 0));
        }
    }

    // EIPs#12473: a wrapper with empty deps uses mode 0 with an empty proofs list; mode 1 with empty deps is invalid.
    [TestCase(MempoolWrapper.ModeDirect, 0, null)]
    [TestCase(MempoolWrapper.ModeDirect, 1, MempoolWrapperValidator.ProofCountMismatch)]
    [TestCase(MempoolWrapper.ModeRecursive, 0, MempoolWrapperValidator.EmptyAggregate)]
    [TestCase(MempoolWrapper.ModeRecursive, 1, MempoolWrapperValidator.EmptyAggregate)]
    public void Empty_deps_wrapper_is_valid_only_in_mode_0(byte mode, int proofBytes, string? expected)
    {
        (MempoolWrapper batch, Func<Hash256, Transaction?> resolve) = Batch(mode, [[]]);
        MempoolWrapper wrapper = new()
        {
            Transactions = batch.Transactions,
            Mode = mode,
            Deps = [],
            Proofs = mode == MempoolWrapper.ModeDirect ? [.. Enumerable.Repeat(new byte[] { 1 }, proofBytes)] : null,
            RecursiveStark = mode == MempoolWrapper.ModeRecursive
                ? new RecursiveStark(new byte[proofBytes], new Hash256(Eip8288Dependencies.ComputeDepsHash([])))
                : null
        };
        FakeLeanProofVerifier verifier = new(true);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, out string? error, resolve);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected is null), error);
            Assert.That(error, Is.EqualTo(expected));
            Assert.That(verifier.VerificationCalls, Is.Zero, "rejected before verification; an empty list has nothing to verify");
        }
    }

    [Test]
    public void Wrapper_transactions_must_be_frame_transactions()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = ByHash(Keccak.Compute("legacy")),
            Mode = MempoolWrapper.ModeDirect,
            Deps = [],
            Proofs = []
        };
        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error, _ => Build.A.Transaction.TestObject), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.NotFrameTransaction));
    }

    [Test]
    public void Preliminary_proof_check_does_not_validate_a_different_dependency_union()
    {
        (MempoolWrapper wrapper, _) = Batch(MempoolWrapper.ModeRecursive, [[Sphincs("claimed")]]);
        FakeLeanProofVerifier verifier = new(true);
        Assert.That(MempoolWrapperValidator.VerifyClaimedProofs(wrapper, verifier, out string? claimError), Is.True, claimError);
        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, out string? error, _ => DepTx([Sphincs("recovered")]), proofsVerified: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsMismatch));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1), "the verified claim is not verified again");
        }
    }

    private static MempoolWrapper RoundTrip(MempoolWrapper wrapper)
    {
        Rlp rlp = MempoolWrapperDecoder.Instance.Encode(wrapper);
        RlpReader reader = new(rlp.Bytes);
        return MempoolWrapperDecoder.Instance.Decode(ref reader)!;
    }
}
