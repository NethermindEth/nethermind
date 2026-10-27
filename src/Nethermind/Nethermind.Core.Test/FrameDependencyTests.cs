// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class FrameDependencyTests
{
    [Test]
    public void Child_serialization_requires_empty_proof_exactly_for_empty_dependencies([Values] bool emptyDependencies, [Values] bool emptyProof)
    {
        RecursiveProofInput child = new(emptyDependencies ? [] : [new FrameDependency(Eip8288Constants.LeanSphincsScheme, default, default)],
            emptyProof ? [] : [1]);
        AggregationInput input = new() { RecursiveProofs = [child] };

        if (emptyDependencies == emptyProof)
            Assert.DoesNotThrow(() => NativeLeanProofVerifier.SerializeInput(input));
        else
            Assert.Throws<ArgumentException>(() => NativeLeanProofVerifier.SerializeInput(input));
    }

    [Test]
    public void ParseFrame_reads_triples_from_frame_data()
    {
        byte[] data = new byte[2 * Eip8288Constants.DependencyTripleLength];
        data[31] = Eip8288Constants.LeanSphincsScheme;
        data[32] = 0xAA;
        data[64] = 0xBB;
        data[96 + 31] = Eip8288Constants.LeanStarkScheme;

        TxFrame frame = new(FrameMode.DepVerify, 0, null, 0, UInt256.Zero, data);
        List<FrameDependency> deps = [.. Eip8288Dependencies.ParseFrame(frame)];

        Assert.That(deps.Count, Is.EqualTo(2));
        Assert.That(deps[0].Scheme, Is.EqualTo(Eip8288Constants.LeanSphincsScheme));
        Assert.That(deps[0].DataHash.Bytes[0], Is.EqualTo(0xAA));
        Assert.That(deps[0].VerificationKey.Bytes[0], Is.EqualTo(0xBB));
        Assert.That(deps[1].Scheme, Is.EqualTo(Eip8288Constants.LeanStarkScheme));
    }

    // EIP-8288 Inclusion Enforcement: a transaction is dependency-bearing iff it has a dependency verification frame, even one
    // carrying no triples, so dependencies(tx) == [] does not make it dependency-free.
    [TestCase(FrameMode.DepVerify, 1, true)]
    [TestCase(FrameMode.DepVerify, 0, true)]
    [TestCase(FrameMode.Verify, 1, false)]
    public void Dependency_bearing_iff_a_dependency_frame_is_present(FrameMode mode, int triples, bool dependencyBearing)
    {
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            Frames = [new(mode, 0, null, 0, UInt256.Zero, new byte[triples * Eip8288Constants.DependencyTripleLength])]
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Dependencies.IsDependencyBearing(transaction), Is.EqualTo(dependencyBearing));
            Assert.That(Eip8288Dependencies.ForTransaction(transaction, static _ => default), Has.Count.EqualTo(mode == FrameMode.DepVerify ? triples : 0));
        }
    }

    [Test]
    public void Serialize_and_Parse_round_trip()
    {
        List<FrameDependency> deps =
        [
            new(Eip8288Constants.LeanSphincsScheme, Keccak.Compute("a"), Keccak.Compute("b")),
            new(Eip8288Constants.LeanStarkScheme, Keccak.Compute("c"), Keccak.Compute("d")),
        ];

        List<FrameDependency> parsed = Eip8288Dependencies.Parse(Eip8288Dependencies.Serialize(deps));

        Assert.That(parsed, Is.EqualTo(deps));
    }

    private static FrameDependency VectorDependency(byte scheme, byte data, int keyStart, byte? keyFill = null)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = keyFill ?? (byte)(keyStart + i);
        return new(scheme, new ValueHash256(Enumerable.Repeat(data, 32).ToArray()), new ValueHash256(key));
    }

    private static readonly FrameDependency VectorA = VectorDependency(Eip8288Constants.LeanSphincsScheme, 0x07, 0);
    private static readonly FrameDependency VectorB = VectorDependency(Eip8288Constants.LeanSphincsScheme, 0x07, 1);
    private static readonly FrameDependency VectorC = VectorDependency(Eip8288Constants.LeanSphincsScheme, 0x01, 0, 0xFF);
    private static readonly FrameDependency VectorD = VectorDependency(Eip8288Constants.LeanSphincsScheme, 0xA5, 0, 0x5A);

    // Expected digests computed in Python from the EIP-8288 get_deps_hash pseudocode with hashlib.blake2s;
    // the pinned recursive guest and tools/lean-ffi assert the same vectors.
    private static IEnumerable<TestCaseData> DepsHashVectors()
    {
        yield return new TestCaseData(Array.Empty<FrameDependency>(), "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9").SetName("empty");
        yield return new TestCaseData(new[] { VectorA }, "6921fc8275ae947e76ab603255f550b615341fb880a8635996d30b55201d28f6").SetName("one");
        yield return new TestCaseData(new[] { VectorB, VectorA, VectorC, VectorA }, "27ad454def73a88020d3f7a6428cd790cf354b531b0447013027e7fa277b9e13").SetName("three_with_duplicate");
        yield return new TestCaseData(new[] { VectorD, VectorA, VectorC, VectorB }, "830b7c9edb9a816e25929dbae987d3fcf2e900fa009d930c60eddacb0d479ed8").SetName("four");
    }

    [TestCaseSource(nameof(DepsHashVectors))]
    public void ComputeDepsHash_matches_eip_pseudocode_vectors(FrameDependency[] dependencies, string expected) =>
        Assert.That(Eip8288Dependencies.ComputeDepsHash(dependencies).ToString(withZeroX: false), Is.EqualTo(expected));

    // get_deps_hash hashes 64-byte data_hash || verification_key_hash entries: the scheme is not bound, which is why only
    // leanSPHINCS is enabled.
    [Test]
    public void Deps_hash_omits_the_scheme() =>
        Assert.That(Eip8288Dependencies.ComputeDepsHash([VectorDependency(Eip8288Constants.LeanStarkScheme, 0x07, 0)]),
            Is.EqualTo(Eip8288Dependencies.ComputeDepsHash([VectorA])));

    [TestCase(Eip8288Constants.LeanSphincsScheme, true)]
    [TestCase(Eip8288Constants.LeanStarkScheme, false)]
    [TestCase((byte)0x00, false)]
    [TestCase((byte)0x12, false)]
    [TestCase((byte)0xFF, false)]
    public void Only_enabled_schemes_are_accepted(byte scheme, bool accepted)
    {
        FrameDependency sphincs = new(Eip8288Constants.LeanSphincsScheme, default, default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Dependencies.IsAcceptedScheme(scheme), Is.EqualTo(accepted));
            Assert.That(Eip8288Dependencies.AreSchemesEnabled([sphincs, new FrameDependency(scheme, default, default)]), Is.EqualTo(accepted));
        }
    }

    // RFC 7693 BLAKE2s-256 at the block boundaries: the empty input is the deps hash of every block without dependencies, and
    // exactly one 64-byte block must be finalized as the last block rather than compressed early.
    [TestCase(0, "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9")]
    [TestCase(64, "56f34e8b96557e90c1f24b52d0c89d51086acf1b00f634cf1dde9233b8eaaa3e")]
    [TestCase(65, "1b53ee94aaf34e4b159d48de352c7f0661d0a40edff95a0b1639b4090e974472")]
    public void Blake2s_matches_reference_digests_at_block_boundaries(int length, string expected)
    {
        byte[] input = [.. Enumerable.Range(0, length).Select(static i => (byte)i)];
        Blake2s pieces = Blake2s.Create();
        for (int offset = 0; offset < input.Length; offset += 7) pieces.Update(input.AsSpan(offset, Math.Min(7, input.Length - offset)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Blake2s.Compute(input).ToString(withZeroX: false), Is.EqualTo(expected));
            Assert.That(pieces.Finish().ToString(withZeroX: false), Is.EqualTo(expected));
        }
    }

    [TestCase(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, "508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982")]
    [TestCase(null, "6d244e1a06ce4ef578dd0f63aff0936706735119ca9c8d22d86c801414ab9741")]
    public void Blake2s_matches_reference_digests(byte[]? input, string expected)
    {
        input ??= Enumerable.Range(0, 200).Select(static i => (byte)i).ToArray();
        Blake2s pieces = Blake2s.Create();
        for (int offset = 0; offset < input.Length; offset += 7) pieces.Update(input.AsSpan(offset, Math.Min(7, input.Length - offset)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Blake2s.Compute(input).ToString(withZeroX: false), Is.EqualTo(expected));
            Assert.That(pieces.Finish().ToString(withZeroX: false), Is.EqualTo(expected));
        }
    }

    [Test]
    public void CountByScheme_counts_each_scheme()
    {
        List<FrameDependency> deps =
        [
            new(Eip8288Constants.LeanSphincsScheme, default, default),
            new(Eip8288Constants.LeanSphincsScheme, default, default),
            new(Eip8288Constants.LeanStarkScheme, default, default),
        ];

        (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(deps);

        Assert.That(sphincs, Is.EqualTo(2));
        Assert.That(stark, Is.EqualTo(1));
    }

    [TestCase(Eip8288Constants.LeanSphincsScheme, Eip8288Constants.LeanSphincsVerificationGas)]
    [TestCase(Eip8288Constants.LeanStarkScheme, Eip8288Constants.LeanStarkVerificationGas)]
    public void VerificationGas_is_per_scheme(byte scheme, ulong expected)
    {
        FrameDependency dep = new(scheme, default, default);
        Assert.That(dep.VerificationGas, Is.EqualTo(expected));
    }

    [Test]
    public void ForTransaction_flattens_only_dependency_frames()
    {
        byte[] depData = new byte[Eip8288Constants.DependencyTripleLength];
        depData[31] = Eip8288Constants.LeanSphincsScheme;

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            Frames =
            [
                new TxFrame(FrameMode.Verify, 0, null, 1, UInt256.Zero, default),
                new TxFrame(FrameMode.DepVerify, 0, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, depData),
            ],
        };

        List<FrameDependency> deps = [.. Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)];

        Assert.That(deps.Count, Is.EqualTo(1));
        Assert.That(deps[0].Scheme, Is.EqualTo(Eip8288Constants.LeanSphincsScheme));
    }

    [Test]
    public void ComputeBlockDepsHash_covers_all_transaction_dependencies()
    {
        Block block = Build.A.Block.WithTransactions(DepTx(Eip8288Constants.LeanSphincsScheme), DepTx(Eip8288Constants.LeanStarkScheme)).TestObject;

        List<FrameDependency> deps = Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue);

        Assert.That(deps.Count, Is.EqualTo(2));
        Assert.That(Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue), Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(deps)));
    }

    [Test]
    public void DependencyCommitment_is_sorted_and_deduplicated()
    {
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
        FrameDependency second = new(Eip8288Constants.LeanStarkScheme, TestItem.KeccakC.ValueHash256, TestItem.KeccakD.ValueHash256);
        List<FrameDependency> canonical = [first, second];
        List<FrameDependency> repeated = [second, first, second, first];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Dependencies.Canonicalize(repeated), Is.EqualTo(canonical));
            Assert.That(Eip8288Dependencies.ComputeDepsHash(repeated), Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(canonical)));
        }
    }

    [Test]
    public void Same_scheme_dependencies_sort_by_message_then_key()
    {
        FrameDependency[] dependencies = [
            new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256),
            new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakC.ValueHash256, TestItem.KeccakA.ValueHash256),
            new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, TestItem.KeccakD.ValueHash256),
            new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakD.ValueHash256, TestItem.KeccakC.ValueHash256)];
        FrameDependency[] expected = dependencies.OrderBy(dep => Convert.ToHexString(Eip8288Dependencies.Serialize([dep])), StringComparer.Ordinal).ToArray();
        List<FrameDependency> reversed = [.. expected.Reverse(), .. expected];
        Assert.That(Eip8288Dependencies.Canonicalize(reversed), Is.EqualTo(expected));
        byte[] entries = [.. expected.SelectMany(static dep => dep.DataHash.ToByteArray().Concat(dep.VerificationKey.ToByteArray()))];
        Assert.That(Eip8288Dependencies.ComputeDepsHash(reversed), Is.EqualTo(Blake2s.Compute(entries)));
    }

    [Test]
    public void Repeated_declarations_share_one_dependency_but_each_pays_gas([Values(1, 2, 17)] int declarations)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
        List<FrameDependency> repeated = [];
        for (int i = 0; i < declarations; i++) repeated.Add(dependency);
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null,
                (ulong)declarations * Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize(repeated))]
        };
        Block block = Build.A.Block.WithTransactions(tx, tx).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue), Has.Count.EqualTo(1));
            Assert.That(Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue), Has.Count.EqualTo(1));
            Assert.That(Eip8288Dependencies.RecursiveStarkGas(tx), Is.EqualTo((ulong)declarations * Eip8288Constants.LeanStarkVerificationGas));
        }
    }

    [Test]
    public void Zero_data_hash_resolves_once_before_canonicalization_without_changing_the_frame([Values(Eip8288Constants.LeanSphincsScheme, Eip8288Constants.LeanStarkScheme)] byte scheme)
    {
        FrameDependency raw = new(scheme, default, TestItem.KeccakA.ValueHash256);
        FrameDependency resolved = new(raw.Scheme, TestItem.KeccakB.ValueHash256, raw.VerificationKey);
        Transaction transaction = DepTx(raw.Scheme);
        byte[] data = Eip8288Dependencies.Serialize([raw, resolved, raw]);
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, data)];
        byte[] encoded = TxDecoder.Instance.Encode(transaction).Bytes;
        int calls = 0;

        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction, _ =>
        {
            calls++;
            return resolved.DataHash;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependencies, Is.EqualTo((FrameDependency[])[resolved]));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(Eip8288Dependencies.ParseFrame(transaction.Frames[0]).First().DataHash, Is.EqualTo(default(ValueHash256)));
            Assert.That(transaction.Frames[0].Data.ToArray(), Is.EqualTo(data));
            Assert.That(TxDecoder.Instance.Encode(transaction).Bytes, Is.EqualTo(encoded));
        }
    }

    [Test]
    public void Block_zero_hashes_use_each_containing_transaction_and_do_not_cache_mutable_signing_fields()
    {
        Transaction first = DepTx(Eip8288Constants.LeanSphincsScheme);
        Transaction second = DepTx(Eip8288Constants.LeanSphincsScheme);
        second.Nonce = 1;
        first.Hash = second.Hash = TestItem.KeccakH;
        Block block = Build.A.Block.WithTransactions(first, second).TestObject;
        int calls = 0;
        ValueHash256 SignatureHash(Transaction transaction)
        {
            calls++;
            return FrameTxSigHash.ComputeValue(transaction);
        }
        List<FrameDependency> dependencies = Eip8288Dependencies.ForBlock(block, SignatureHash);
        FrameDependency[] expected = [new(Eip8288Constants.LeanSphincsScheme, FrameTxSigHash.ComputeValue(first), default),
            new(Eip8288Constants.LeanSphincsScheme, FrameTxSigHash.ComputeValue(second), default)];
        Assert.That(dependencies, Is.EqualTo(Eip8288Dependencies.Canonicalize(expected)));
        Assert.That(calls, Is.EqualTo(2));

        first.Nonce = 2;
        List<FrameDependency> changed = Eip8288Dependencies.ForTransaction(first, FrameTxSigHash.ComputeValue);
        Assert.That(changed[0].DataHash, Is.EqualTo(FrameTxSigHash.ComputeValue(first)));
        Assert.That(changed[0].DataHash, Is.Not.EqualTo(expected[0].DataHash));
        Assert.That(first.Hash, Is.EqualTo(TestItem.KeccakH), "dependency resolution does not trust the envelope-hash cache");
    }

    [Test]
    public void Explicit_data_hashes_do_not_compute_the_signature_hash()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanStarkScheme, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
        Transaction transaction = DepTx(dependency.Scheme);
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))];
        static ValueHash256 UnexpectedHash(Transaction _) => throw new InvalidOperationException("explicit dependency must remain unchanged");
        Assert.That(Eip8288Dependencies.ForTransaction(transaction, UnexpectedHash), Is.EqualTo((FrameDependency[])[dependency]));
        Assert.That(Eip8288Dependencies.ForBlock(Build.A.Block.WithTransactions(transaction).TestObject, UnexpectedHash),
            Is.EqualTo((FrameDependency[])[dependency]));
    }

    private static Transaction DepTx(byte scheme)
    {
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = scheme;
        return new Transaction
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(FrameMode.DepVerify, 0, null, 0, UInt256.Zero, data)],
        };
    }
}
