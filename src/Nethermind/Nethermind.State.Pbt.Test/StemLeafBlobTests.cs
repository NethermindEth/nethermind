// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class StemLeafBlobTests
{
    /// <summary>The layouts a rebuild writes; the legacy one is only ever read.</summary>
    private static readonly PbtLeafFormat[] Formats =
        [PbtLeafFormat.EveryLevel, PbtLeafFormat.Interleaved, PbtLeafFormat.LeavesOnly];

    /// <summary>The leaf values the <see cref="LegacyFixtures"/> blobs were written with, in ascending sub-index order.</summary>
    private static readonly byte[][] FixtureValues =
    [
        Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111"),
        Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222"),
        Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333"),
    ];

    // Blobs written by the legacy layout, which cached every single-child internal. They are checked in
    // rather than generated because nothing writes that format any more: reproducing one means reverting
    // the layout change. Sub-index sets chosen for their internal shape — a lone leaf whose every ancestor
    // is single-child, a pair split across the root so that the root alone branches, and a mixed set whose
    // branching internals sit at two different depths.
    private const string LegacySingleLeaf =
        "0x111111111111111111111111111111111111111111111111111111111111111100cb4bb82263b23f0e7ccf04a6168c1877536fb1e676850759517c266daa00219731c12972bf86bf19768d53dc26304cb17f07bc4fade2c3cbd561402d054f29c2e5351ea461ba80e56262935c9f7d8a284ea0e1e044cf431d4d177bfce548561295b0702c1599d75bfeae048ec41a75d5112c5e0d0692db6294457960a77d0d36051a7d4b0374f95b5d70fdf6d1d19ddf70a7f9d939500b58f9fede17223ed285ac2f41f14f3db0c960693bc36add419afab32ae70173d09353f0553788f6e2f5e2ceb766cfcebb302d6cfae06bce8a7f8188d5303e079ed69f4b025fa76d0a35b1b3266b3d1170fdcdc5f089fc1c1ccd09401ee206b6628d2ac5828f8d598f8000010001";

    private const string LegacyTwoScattered =
        "0x111111111111111111111111111111111111111111111111111111111111111176ce668cbd1ef85e0d569cf09b18cb58a9f769f5b71b7ea37df79ddcb4875eddbc8d7653929d8662f4c2d7c5720a98df743a93a9a36ef6fb7e2db92f6657b3b4f1195917f4741e310c9b9a8d55dea045ac31c214ebf082ed50bb0ad6b5df7c9a52672c4f31a6d5b7a2d8992ba6ff646d5eaaa25f2289599fb3593f4113df34411811b4676f2f1c216154583c70dcbeec663a0f5de757bc705386ed1c76d265459fc0913c4e7905738fb71e343865afe2ca906320dc81d57ee67d53fc4e25f487bb9d7ab90a71a8f12284e38ea368a4cd5bfec140235f6deefbe3d5cded3ff60b2222222222222222222222222222222222222222222222222222222222222222430196e75e30b577c2762d5fb44808b3f380eced1e332adc5bad0c41e94b2c8000c25997aae61dc16bc31aadd342e696e0aeb324e25bfbd16cd3329337ef7a5ba12dcda70c5ad007ab3f4167e63b32a6af8c5c546e6cf0480ba990c929119b0c3f5c54394a84a888539e4c7cc3faf9059500ff1ee32900f6677e92193a9a3b470ee0ad18b7c853b7144dbc12b859bdb9b88f7bf21579bf51802d65e09ab0ce0769a1b6fd268cead387adc87f419b950709d822727a2b3b6d343576416fbba57ecf16caabc2049d6184485fa650fff384ffd9c0de74c9fc4fefd65830af05258a2156055d27b7484a8828d39464f31181d8eb1331be23ff52b077a2b633e39adb04000080011001";

    private const string LegacyThreeMixed =
        "0x111111111111111111111111111111111111111111111111111111111111111100cb4bb82263b23f0e7ccf04a6168c1877536fb1e676850759517c266daa00212222222222222222222222222222222222222222222222222222222222222222430196e75e30b577c2762d5fb44808b3f380eced1e332adc5bad0c41e94b2c8076e242fbe9c821ef3363ff57cf7a0d7fa125c3b9d90320e7e34f445818fbd5f93c85b930d24d312cf33fd9a73eb97a52e0fd4e3a34ee3dfbc04f38c051dd6a4335815a31f02ef746e808833308518cf4bbe497b4bcc56acf7378abd138d319aa310c950f0240f64a1e49d708aed30324f860c5c10d54b321953c016ab35bb37f1b8597403029b510fe6d95de2692fa973af693cebc1786bad7f2e7727d5dd8fa3333333333333333333333333333333333333333333333333333333333333333b8603d374dbfa80b0740b12d8f151ad5cb49c4ff3b6bf23c700f7b1888b36f916d7289fbab085984e666ade1d5889933395ce77bbabfb843e0483e48d8fceae4cbf320df7fe385e229790a052b5fa070b363fc7d56fd0ebe0cea0007a87fd24b80675baffa049261b130b4115c4e0de32d5352ce8f0029e8bbb31683b1e4c7b1e769e23a6d83943f0926d65c701f4cbc97fde1ac1f2b8ec04584cb34f5466d92db5f994d46af357850d50b3d693c633af7ef4702520777ffca3423f0a2d3635aa05e20b090b290c54aa69d531300fb75a677394d81ef5aa7f95d60c63391a5589bbe43d5871b3222dd2b2f1ed2bf14a7ea78e51a390e876594b6f80d61f12b6ca0000800410001";

    [TestCase(PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.Interleaved)]
    [TestCase(PbtLeafFormat.LeavesOnly)]
    public void ApplyReadBackClearAndDeletionSignal(PbtLeafFormat format)
    {
        byte[] value5 = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] value200 = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        byte[] blob = Apply([], new Dictionary<byte, byte[]?> { [5] = value5, [200] = value200 }, format, out _);
        Assert.That(StemLeafBlob.TryGetValue(blob, 5, out ReadOnlySpan<byte> read5) && read5.SequenceEqual(value5));
        Assert.That(StemLeafBlob.TryGetValue(blob, 200, out ReadOnlySpan<byte> read200) && read200.SequenceEqual(value200));
        Assert.That(StemLeafBlob.TryGetValue(blob, 6, out _), Is.False);

        blob = Apply(blob, new Dictionary<byte, byte[]?> { [5] = null }, format, out _);
        Assert.That(StemLeafBlob.TryGetValue(blob, 5, out _), Is.False);
        Assert.That(StemLeafBlob.TryGetValue(blob, 200, out read200) && read200.SequenceEqual(value200));

        // an all-zero value clears too, and an empty blob signals stem deletion with a zero root
        blob = Apply(blob, new Dictionary<byte, byte[]?> { [200] = new byte[32] }, format, out ValueHash256 emptyRoot);
        Assert.That(blob, Is.Empty);
        Assert.That(emptyRoot, Is.EqualTo(default(ValueHash256)));
    }

    /// <summary>
    /// Which levels each layout keeps, pinned because the interleaved counter unrolls exactly its
    /// three: a level added to or taken off that mask has to be added there as well.
    /// </summary>
    [TestCase(PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.Interleaved)]
    [TestCase(PbtLeafFormat.LeavesOnly)]
    public void KeptLevels(PbtLeafFormat format)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int width = 2; width <= 256; width *= 2)
            {
                Assert.That(
                    PbtLayout.StemLeafStoresInternalAtWidth(format, width),
                    Is.EqualTo(format switch
                    {
                        PbtLeafFormat.Interleaved => width is 4 or 16 or 64,
                        PbtLeafFormat.LeavesOnly => false,
                        _ => true,
                    }),
                    $"width {width}");
            }
        }
    }

    private static IEnumerable<TestCaseData> EntryCounts()
    {
        byte[] adjacent = [0, 1];
        byte[] scattered = [5, 200];
        byte[] dense = new byte[256];
        for (int i = 0; i < dense.Length; i++) dense[i] = (byte)i;

        // n leaves cost 2n-1 entries at every level. Interleaving keeps only the branching nodes of the
        // 4-, 16- and 64-wide levels and no root at all, so a pair of leaves — branching one level above
        // themselves, and single-child all the way up from there — costs nothing but the leaves, and a
        // full stem costs 64 + 16 + 4. The leaves-only layout costs the leaves and nothing else, whatever
        // their shape.
        yield return new TestCaseData(adjacent, PbtLeafFormat.EveryLevel, 3).SetName("AdjacentPairEveryLevel");
        yield return new TestCaseData(adjacent, PbtLeafFormat.Interleaved, 2).SetName("AdjacentPairInterleaved");
        yield return new TestCaseData(scattered, PbtLeafFormat.EveryLevel, 3).SetName("ScatteredPairEveryLevel");
        yield return new TestCaseData(scattered, PbtLeafFormat.Interleaved, 2).SetName("ScatteredPairInterleaved");
        yield return new TestCaseData(dense, PbtLeafFormat.EveryLevel, 511).SetName("DenseEveryLevel");
        yield return new TestCaseData(dense, PbtLeafFormat.Interleaved, 256 + 64 + 16 + 4).SetName("DenseInterleaved");
        yield return new TestCaseData(adjacent, PbtLeafFormat.LeavesOnly, 2).SetName("AdjacentPairLeavesOnly");
        yield return new TestCaseData(scattered, PbtLeafFormat.LeavesOnly, 2).SetName("ScatteredPairLeavesOnly");
        yield return new TestCaseData(dense, PbtLeafFormat.LeavesOnly, 256).SetName("DenseLeavesOnly");
    }

    /// <remarks>
    /// A leaf's entry is found by counting the entries before it rather than by an offset of its own, so
    /// the read back is what proves the count the layout implies is the count the fold emitted.
    /// </remarks>
    [TestCaseSource(nameof(EntryCounts))]
    public void StoredEntryCountAndReadBack(byte[] subIndices, PbtLeafFormat format, int expectedEntries)
    {
        Random rng = new(subIndices.Length);
        Dictionary<byte, byte[]?> values = [];
        foreach (byte subIndex in subIndices) values[subIndex] = RandomValue(rng);

        byte[] blob = Apply([], values, format, out _);

        bool[] occupied = new bool[16];
        foreach (byte subIndex in subIndices) occupied[subIndex >> 4] = true;
        int occupiedGroups = 0;
        foreach (bool group in occupied)
        {
            if (group) occupiedGroups++;
        }

        using (Assert.EnterMultipleScope())
        {
            // entries + subwords(G) + top + format byte
            Assert.That(blob, Has.Length.EqualTo(expectedEntries * 32 + 2 * occupiedGroups + 2 + 1));
            foreach ((byte subIndex, byte[]? expected) in values)
            {
                Assert.That(
                    StemLeafBlob.TryGetValue(blob, subIndex, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(expected!),
                    Is.True,
                    $"sub-index {subIndex}");
            }
        }
    }

    /// <summary>
    /// A leaf's entry is located by counting the entries of the subtrees to its left, which the
    /// interleaved layout counts by a level-at-a-time bit fold rather than by walking. That is only
    /// exercised by reading a leaf out of a shape whose levels differ, so this sweeps the whole density
    /// range: a count right for a dense stem alone would pass every fixed shape above.
    /// </summary>
    [TestCase(PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.Interleaved)]
    [TestCase(PbtLeafFormat.LeavesOnly)]
    public void EveryLeafReadsBackAcrossRandomShapes(PbtLeafFormat format)
    {
        Random rng = new(4242);
        for (int leafCount = 1; leafCount <= 256; leafCount++)
        {
            Dictionary<byte, byte[]?> values = [];
            for (int i = 0; i < leafCount; i++) values[(byte)rng.Next(256)] = RandomValue(rng);

            byte[] blob = Apply([], values, format, out _);
            foreach ((byte subIndex, byte[]? expected) in values)
            {
                Assert.That(
                    StemLeafBlob.TryGetValue(blob, subIndex, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(expected!),
                    Is.True,
                    $"{values.Count} leaves, sub-index {subIndex}");
            }
        }
    }

    /// <summary>
    /// The layouts describe the same subtree, so they interoperate: a blob written in one and rewritten
    /// in the other must come out byte-identical to a fresh fold in the new one — the copy-verbatim path
    /// has to refold across the change rather than splice the old run into the new blob.
    /// </summary>
    [TestCase(PbtLeafFormat.EveryLevel, PbtLeafFormat.Interleaved)]
    [TestCase(PbtLeafFormat.Interleaved, PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.EveryLevel, PbtLeafFormat.LeavesOnly)]
    [TestCase(PbtLeafFormat.LeavesOnly, PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.Interleaved, PbtLeafFormat.LeavesOnly)]
    [TestCase(PbtLeafFormat.LeavesOnly, PbtLeafFormat.Interleaved)]
    public void MixedFormatRewriteMatchesAFreshFoldInTheNewFormat(PbtLeafFormat initial, PbtLeafFormat then)
    {
        Random rng = new(2027);
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));

        // scattered enough that the rewrite below leaves whole clean subtrees for the copy path to take
        Dictionary<byte, byte[]?> values = [];
        for (int i = 0; i < 40; i++) values[(byte)rng.Next(256)] = RandomValue(rng);

        byte[] blob = Apply([], values, initial, out _);
        Assert.That(blob[^1], Is.EqualTo((byte)initial));

        byte[] changed = RandomValue(rng);
        values[117] = changed;
        byte[] rewritten = Apply(blob, new Dictionary<byte, byte[]?> { [117] = changed }, then, out ValueHash256 root);
        byte[] fresh = Apply([], values, then, out ValueHash256 freshRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(fresh), $"a rewrite must match a fresh {then} fold, not splice {initial} bytes");
            Assert.That(root, Is.EqualTo(freshRoot));
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, root), Is.EqualTo(BuildOracleRoot(stem, values)));
        }
    }

    [TestCase(1, 1, PbtLeafFormat.EveryLevel)]
    [TestCase(1, 1, PbtLeafFormat.Interleaved)]
    [TestCase(1, 1, PbtLeafFormat.LeavesOnly)]
    [TestCase(7, 40, PbtLeafFormat.EveryLevel)]
    [TestCase(7, 40, PbtLeafFormat.Interleaved)]
    [TestCase(7, 40, PbtLeafFormat.LeavesOnly)]
    [TestCase(99, 256, PbtLeafFormat.EveryLevel)]
    [TestCase(99, 256, PbtLeafFormat.Interleaved)]
    [TestCase(99, 256, PbtLeafFormat.LeavesOnly)]
    public void SubtreeRootAndStemNodeHashMatchEipReference(int seed, int leafCount, PbtLeafFormat format)
    {
        Random rng = new(seed);
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));

        Dictionary<byte, byte[]?> changes = [];
        EipReferenceTree reference = new();
        for (int i = 0; i < leafCount; i++)
        {
            byte subIndex = (byte)rng.Next(256);
            byte[] value = new byte[32];
            rng.NextBytes(value);
            changes[subIndex] = value;
        }

        foreach ((byte subIndex, byte[]? value) in changes)
        {
            reference.Insert([.. stem.Bytes, subIndex], value!);
        }

        Apply([], changes, format, out ValueHash256 subtreeRoot);

        // a single-stem reference tree's root is exactly the stem node hash
        ValueHash256 stemNodeHash = StemLeafBlob.ComputeStemNodeHash(stem, subtreeRoot);
        Assert.That(stemNodeHash, Is.EqualTo(new ValueHash256(reference.Merkelize())));
    }

    [TestCase(3, PbtLeafFormat.EveryLevel)]
    [TestCase(3, PbtLeafFormat.Interleaved)]
    [TestCase(3, PbtLeafFormat.LeavesOnly)]
    [TestCase(17, PbtLeafFormat.EveryLevel)]
    [TestCase(17, PbtLeafFormat.Interleaved)]
    [TestCase(17, PbtLeafFormat.LeavesOnly)]
    [TestCase(101, PbtLeafFormat.EveryLevel)]
    [TestCase(101, PbtLeafFormat.Interleaved)]
    [TestCase(101, PbtLeafFormat.LeavesOnly)]
    public void IncrementalApplyMatchesFromScratchAndEipReference(int seed, PbtLeafFormat format)
    {
        Random rng = new(seed);
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));
        Dictionary<byte, byte[]?> finalValues = [];
        byte[] incrementalBlob = [];
        ValueHash256 incrementalRoot = default;

        for (int batchIndex = 0; batchIndex < 6; batchIndex++)
        {
            Dictionary<byte, byte[]?> batch = [];
            for (int operation = 0; operation < 64; operation++)
            {
                byte subIndex = (byte)rng.Next(2, 256);
                batch[subIndex] = batchIndex != 0 && rng.Next(4) == 0 ? null : RandomValue(rng);
            }

            if (batchIndex == 0) batch[0] = RandomValue(rng);
            if (batchIndex == 1) batch[0] = null;
            batch[255] = RandomValue(rng);
            incrementalBlob = Apply(incrementalBlob, batch, format, out incrementalRoot);
            foreach ((byte subIndex, byte[]? value) in batch)
            {
                if (value is null)
                {
                    finalValues.Remove(subIndex);
                }
                else
                {
                    finalValues[subIndex] = value;
                }
            }
        }

        byte[] fromScratchBlob = Apply([], finalValues, format, out ValueHash256 fromScratchRoot);
        ValueHash256 oracleRoot = BuildOracleRoot(stem, finalValues);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(incrementalBlob, Is.EqualTo(fromScratchBlob));
            Assert.That(incrementalRoot, Is.EqualTo(fromScratchRoot));
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, incrementalRoot), Is.EqualTo(oracleRoot));
        }

        foreach ((byte subIndex, byte[]? expected) in finalValues)
        {
            Assert.That(
                StemLeafBlob.TryGetValue(incrementalBlob, subIndex, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(expected),
                Is.True,
                $"sub-index {subIndex}");
        }
    }

    [TestCase(PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.Interleaved)]
    [TestCase(PbtLeafFormat.LeavesOnly)]
    public void SingleLeafChangeOnDenseStemMatchesEipReference(PbtLeafFormat format)
    {
        Random rng = new(2026);
        Stem stem = new(Bytes.FromHexString("0xff112233445566778899aabbccddeeff00112233445566778899aabbccddee"));
        Dictionary<byte, byte[]?> values = [];
        for (int subIndex = 0; subIndex < 256; subIndex++)
        {
            values[(byte)subIndex] = RandomValue(rng);
        }

        byte[] blob = Apply([], values, format, out _);
        byte[] changedValue = RandomValue(rng);
        values[117] = changedValue;
        blob = Apply(blob, new Dictionary<byte, byte[]?> { [117] = changedValue }, format, out ValueHash256 subtreeRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, subtreeRoot), Is.EqualTo(BuildOracleRoot(stem, values)));
            Assert.That(StemLeafBlob.TryGetValue(blob, 117, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(changedValue));
        }
    }

    private static IEnumerable<TestCaseData> SparseLeafSets()
    {
        foreach (PbtLeafFormat format in Formats)
        {
            yield return new TestCaseData(new byte[] { 0, 255 }, (byte)255, format).SetName($"WidestChains{format}");
            yield return new TestCaseData(new byte[] { 0, 1 }, (byte)1, format).SetName($"BranchingAtTheDeepestInternal{format}");
            yield return new TestCaseData(new byte[] { 5, 200 }, (byte)5, format).SetName($"BranchingRoot{format}");
            yield return new TestCaseData(new byte[] { 0, 2, 100 }, (byte)2, format).SetName($"CleanSingleChildSibling{format}");
        }
    }

    /// <summary>
    /// Sparse stems are where single-child chains dominate; the random cases above are far too dense to reach
    /// them. Deleting collapses a branching internal to single-child and re-inserting restores it, so the
    /// round trip covers both transitions.
    /// </summary>
    /// <remarks>
    /// The <c>{0, 2, 100}</c> case pins the clean-copy guard: deleting leaf 2 leaves <c>[0, 2)</c> both clean
    /// and single-child, and copying it wholesale would hand leaf 0's raw value up as a cached internal hash.
    /// </remarks>
    [TestCaseSource(nameof(SparseLeafSets))]
    public void SparseStemsSurviveDeletionAndReinsertion(byte[] subIndices, byte deleted, PbtLeafFormat format)
    {
        Random rng = new(subIndices.Length * 31 + deleted);
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));

        Dictionary<byte, byte[]?> values = [];
        foreach (byte subIndex in subIndices)
        {
            values[subIndex] = RandomValue(rng);
        }

        byte[] original = Apply([], values, format, out ValueHash256 originalRoot);
        Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, originalRoot), Is.EqualTo(BuildOracleRoot(stem, values)), "from scratch");

        byte[]? deletedValue = values[deleted];
        values.Remove(deleted);
        byte[] blob = Apply(original, new Dictionary<byte, byte[]?> { [deleted] = null }, format, out ValueHash256 root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, root), Is.EqualTo(BuildOracleRoot(stem, values)), "after deletion");
            Assert.That(blob, Is.EqualTo(Apply([], values, format, out _)), "after deletion, incremental matches from scratch");
            foreach ((byte subIndex, byte[]? expected) in values)
            {
                Assert.That(
                    StemLeafBlob.TryGetValue(blob, subIndex, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(expected!),
                    Is.True,
                    $"sub-index {subIndex}");
            }
        }

        blob = Apply(blob, new Dictionary<byte, byte[]?> { [deleted] = deletedValue }, format, out root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blob, Is.EqualTo(original), "re-inserting restores the blob the deletion collapsed");
            Assert.That(root, Is.EqualTo(originalRoot));
        }
    }

    private static IEnumerable<TestCaseData> LegacyFixtures()
    {
        foreach (PbtLeafFormat format in Formats)
        {
            yield return new TestCaseData(new byte[] { 0 }, LegacySingleLeaf, format).SetName($"LegacySingleLeaf{format}");
            yield return new TestCaseData(new byte[] { 5, 200 }, LegacyTwoScattered, format).SetName($"LegacyTwoScattered{format}");
            yield return new TestCaseData(new byte[] { 0, 2, 100 }, LegacyThreeMixed, format).SetName($"LegacyThreeMixed{format}");
        }
    }

    [TestCaseSource(nameof(LegacyFixtures))]
    public void LegacyBlobReadsBackAndUpgradesOnWrite(byte[] subIndices, string legacyHex, PbtLeafFormat format)
    {
        Stem stem = new(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddee"));
        byte[] legacyBlob = Bytes.FromHexString(legacyHex);

        Dictionary<byte, byte[]?> values = [];
        for (int i = 0; i < subIndices.Length; i++)
        {
            values[subIndices[i]] = FixtureValues[i];
        }

        Assert.That(TwoLevelBitmapReader.FormatOf(legacyBlob), Is.EqualTo(PbtLeafFormat.Legacy), "the fixture must be in the legacy format");
        foreach ((byte subIndex, byte[]? expected) in values)
        {
            Assert.That(
                StemLeafBlob.TryGetValue(legacyBlob, subIndex, out ReadOnlySpan<byte> actual) && actual.SequenceEqual(expected!),
                Is.True,
                $"legacy read of sub-index {subIndex}");
        }

        // an unchanged rebuild is a pure conversion; a changed one rebuilds the legacy prior as it converts
        byte[] upgraded = Apply(legacyBlob, [], format, out ValueHash256 upgradedRoot);
        byte[] fromScratch = Apply([], values, format, out ValueHash256 fromScratchRoot);

        Dictionary<byte, byte[]?> extended = new(values) { [7] = FixtureValues[^1] };
        byte[] added = Apply(legacyBlob, new Dictionary<byte, byte[]?> { [7] = FixtureValues[^1] }, format, out ValueHash256 addedRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(upgraded[^1], Is.EqualTo((byte)format), "a rebuild always writes the format it was handed");
            Assert.That(upgraded, Is.EqualTo(fromScratch), "the upgrade matches a from-scratch build");
            Assert.That(upgradedRoot, Is.EqualTo(fromScratchRoot));
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, upgradedRoot), Is.EqualTo(BuildOracleRoot(stem, values)));

            Assert.That(added, Is.EqualTo(Apply([], extended, format, out _)), "a write against a legacy prior matches a from-scratch build");
            Assert.That(StemLeafBlob.ComputeStemNodeHash(stem, addedRoot), Is.EqualTo(BuildOracleRoot(stem, extended)));
        }
    }

    private static ValueHash256 BuildOracleRoot(Stem stem, Dictionary<byte, byte[]?> values)
    {
        EipReferenceTree reference = new();
        foreach ((byte subIndex, byte[]? value) in values)
        {
            reference.Insert([.. stem.Bytes, subIndex], value!);
        }

        return new ValueHash256(reference.Merkelize());
    }

    private static byte[] RandomValue(Random rng)
    {
        byte[] value = new byte[32];
        rng.NextBytes(value);
        value[0] |= 1;
        return value;
    }

    /// <summary>Maps the byte[] changes to 32-byte leaf values (null/empty = clear) and applies them.</summary>
    private static byte[] Apply(
        ReadOnlySpan<byte> prior, Dictionary<byte, byte[]?> changes, PbtLeafFormat format, out ValueHash256 subtreeRoot)
    {
        IPbtStemChanges mapped = PbtStemChanges.Rent();
        foreach ((byte subIndex, byte[]? value) in changes)
        {
            ValueHash256 leaf = default;
            value?.CopyTo(leaf.BytesAsSpan);
            mapped = mapped.Set(subIndex, leaf);
        }

        using StemLeafBlob.RebuildState rebuilt = StemLeafBlob.Apply(prior, mapped, PooledRefCountingMemoryProvider.Instance, format);
        subtreeRoot = rebuilt.SubtreeRoot;
        byte[] result = rebuilt.Blob.ToArray();
        PbtStemChanges.Return(mapped);
        return result;
    }
}
