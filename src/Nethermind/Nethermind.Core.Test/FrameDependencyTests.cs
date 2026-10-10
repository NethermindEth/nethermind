// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class FrameDependencyTests
{
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
    private static readonly FrameDependency VectorG1 = VectorDependency(Eip8288Constants.LeanStarkScheme, 0x07, 0);
    private static readonly FrameDependency VectorG2 = VectorDependency(Eip8288Constants.LeanStarkScheme, 0xA5, 0, 0x5A);

    // Expected digests computed in Python from the EIP-8288 get_deps_hash pseudocode with hashlib.blake2s;
    // the pinned recursive guest and tools/lean-ffi assert the same vectors.
    private static IEnumerable<TestCaseData> DepsHashVectors()
    {
        yield return new TestCaseData(Array.Empty<FrameDependency>(), "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9").SetName("empty");
        yield return new TestCaseData(new[] { VectorA }, "aef5a1c0fbd299e930670837a1d8525a37ebfbb3139edfa972cb84788484afe6").SetName("one");
        yield return new TestCaseData(new[] { VectorB, VectorA, VectorC, VectorA }, "db319771cd52a8a6eadb4273e07df5fbf2a405592e956e3fe6f3121f9cb3b897").SetName("three_with_duplicate");
        yield return new TestCaseData(new[] { VectorG1 }, "2eacc579313017124720641cf33287809cb02437e8f381c427e0f5aa8162db93").SetName("stark_only");
        yield return new TestCaseData(new[] { VectorG2, VectorA, VectorG1, VectorC }, "2523de87398f08d0bb08e78b1ff651b0452a1c12fe8dbae806a4c8ffe7fbaf31").SetName("mixed");
    }

    [TestCaseSource(nameof(DepsHashVectors))]
    public void ComputeDepsHash_matches_eip_pseudocode_vectors(FrameDependency[] dependencies, string expected) =>
        Assert.That(Eip8288Dependencies.ComputeDepsHash(dependencies).ToString(withZeroX: false), Is.EqualTo(expected));

    [Test]
    public void Same_data_and_key_under_another_scheme_is_another_digest() =>
        Assert.That(Eip8288Dependencies.ComputeDepsHash([VectorG1]), Is.Not.EqualTo(Eip8288Dependencies.ComputeDepsHash([VectorA])));

    [TestCase(Eip8288Constants.LeanSphincsScheme, true)]
    [TestCase(Eip8288Constants.LeanStarkScheme, Eip8288Constants.LeanStarkSchemeEnabled)]
    [TestCase((byte)0x00, false)]
    [TestCase((byte)0x12, false)]
    [TestCase((byte)0xFF, false)]
    public void Only_enabled_schemes_are_accepted(byte scheme, bool accepted) =>
        Assert.That(Eip8288Dependencies.IsAcceptedScheme(scheme), Is.EqualTo(accepted));

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

        List<FrameDependency> deps = [.. Eip8288Dependencies.ForTransaction(tx)];

        Assert.That(deps.Count, Is.EqualTo(1));
        Assert.That(deps[0].Scheme, Is.EqualTo(Eip8288Constants.LeanSphincsScheme));
    }

    [Test]
    public void ComputeBlockDepsHash_covers_all_transaction_dependencies()
    {
        Block block = Build.A.Block.WithTransactions(DepTx(Eip8288Constants.LeanSphincsScheme), DepTx(Eip8288Constants.LeanStarkScheme)).TestObject;

        List<FrameDependency> deps = Eip8288Dependencies.ForBlock(block);

        Assert.That(deps.Count, Is.EqualTo(2));
        Assert.That(Eip8288Dependencies.ComputeBlockDepsHash(block), Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(deps)));
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
        byte[] entries = Eip8288Dependencies.Serialize(expected);
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
            Assert.That(Eip8288Dependencies.ForTransaction(tx), Has.Count.EqualTo(1));
            Assert.That(Eip8288Dependencies.ForBlock(block), Has.Count.EqualTo(1));
            Assert.That(Eip8288Dependencies.RecursiveStarkGas(tx), Is.EqualTo((ulong)declarations * Eip8288Constants.LeanStarkVerificationGas));
        }
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
