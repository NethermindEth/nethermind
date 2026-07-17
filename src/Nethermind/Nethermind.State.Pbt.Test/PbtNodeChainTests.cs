// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeChainTests
{
    private const int StartDepth = 8;
    private const int TargetDepth = 20;

    // zero past TargetDepth, as a group's path is; bits 8..20 are the chain's own single-child path
    private static readonly Stem TargetPath = new(Bytes.FromHexString("0x0dead000000000000000000000000000000000000000000000000000000000"));
    private static readonly ValueHash256 TargetHash = new(Bytes.FromHexString("0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));

    [Test]
    public void WriteDecodeRoundTrip_AndDiscriminatesFromAGroup()
    {
        byte[] encoded = Encode(StartDepth, TargetDepth, TargetPath, TargetHash);
        Assert.That(encoded, Has.Length.EqualTo(PbtNodeChain.EncodedLength));

        PbtNodeChain chain = PbtNodeChain.Decode(encoded, StartDepth);
        Assert.That(chain.StartDepth, Is.EqualTo(StartDepth));
        Assert.That(chain.TargetDepth, Is.EqualTo(TargetDepth));
        Assert.That(chain.TargetPath, Is.EqualTo(TargetPath));
        Assert.That(chain.TargetHash, Is.EqualTo(TargetHash));
        Assert.That(chain.TargetKey, Is.EqualTo(new TrieNodeKey(TargetDepth, TargetPath)));

        // the cached node hash is the fold, and PbtNodeChain.NodeHashOf reads it without validating
        ValueHash256 folded = PbtNodeChain.Fold(TargetHash, TargetPath, TargetDepth, StartDepth);
        Assert.That(chain.NodeHash, Is.EqualTo(folded));
        Assert.That(PbtNodeChain.NodeHashOf(encoded), Is.EqualTo(folded));

        // the two blob formats sharing the store's column are told apart by their leading byte alone
        Assert.That(PbtNodeChain.IsChain(encoded));
        Assert.That(() => PbtTrieNodeGroup.Decode(encoded), Throws.TypeOf<InvalidDataException>());
        Assert.That(PbtNodeChain.IsChain(EncodeGroup()), Is.False);
        Assert.That(PbtNodeChain.IsChain([]), Is.False, "an absent blob is neither");
    }

    /// <summary>
    /// The fold reproduces the reference tree's merkelisation of a spine: two stems diverging at
    /// <paramref name="divergenceBit"/> make the trie a single-child path from the root down to it, so
    /// the whole root is the branch node folded back up — which is exactly what a chain stores.
    /// </summary>
    [TestCase(1)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(63)]
    [TestCase(247)]
    public void Fold_MatchesTheReferenceSpine(int divergenceBit)
    {
        byte[] stemA = new byte[31];
        byte[] stemB = new byte[31];
        stemB[divergenceBit >> 3] = (byte)(1 << (7 - (divergenceBit & 7)));
        byte[] keyA = [.. stemA, 5];
        byte[] keyB = [.. stemB, 7];
        byte[] valueA = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueB = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        // a lone stem merkelises to its stem node hash, with nothing folded above it
        ValueHash256 hashA = ReferenceRoot([(keyA, valueA)]);
        ValueHash256 hashB = ReferenceRoot([(keyB, valueB)]);

        // stemA is all zeros, so it takes the left of the branch and its path is the spine's
        Assert.That(new Stem(stemA).GetBit(divergenceBit), Is.Zero);
        ValueHash256 branch = Blake3Hash.HashPairOrZero(hashA, hashB);

        Assert.That(
            PbtNodeChain.Fold(branch, new Stem(stemA), divergenceBit, 0),
            Is.EqualTo(ReferenceRoot([(keyA, valueA), (keyB, valueB)])));
    }

    private static readonly object[] Rejections =
    [
        new object[] { "a group's format byte", Corrupt(0, 0x01) },
        new object[] { "an unknown format byte", Corrupt(0, 0xFF) },
        new object[] { "a misaligned target depth", Corrupt(1, TargetDepth + 1) },
        new object[] { "a target at the start depth", Corrupt(1, StartDepth) },
        new object[] { "a target above the start depth", Corrupt(1, StartDepth - 4) },
        new object[] { "a target past the deepest group", Corrupt(1, Stem.LengthInBits) },
        new object[] { "a target path with bits past the target", Corrupt(4, 0x01) },
        new object[] { "the empty subtree as a target hash", Zero(2 + Stem.Length, 32) },
    ];

    [TestCaseSource(nameof(Rejections))]
    public void Decode_Rejects(string description, Func<byte[], byte[]> corrupt)
    {
        byte[] corrupted = corrupt(Encode(StartDepth, TargetDepth, TargetPath, TargetHash));
        Assert.That(() => PbtNodeChain.Decode(corrupted, StartDepth), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Decode_RejectsAWrongLengthOrARootStart()
    {
        byte[] valid = Encode(StartDepth, TargetDepth, TargetPath, TargetHash);
        Assert.That(() => PbtNodeChain.Decode(valid.AsSpan(..^1), StartDepth), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => PbtNodeChain.Decode([.. valid, 0], StartDepth), Throws.TypeOf<InvalidDataException>());

        // the root group keeps its own spine, so nothing chains from depth 0 (invariant 4)
        Assert.That(() => PbtNodeChain.Decode(valid, 0), Throws.TypeOf<InvalidDataException>());
    }

    private static Func<byte[], byte[]> Corrupt(int offset, int value) => blob =>
    {
        blob[offset] = (byte)value;
        return blob;
    };

    private static Func<byte[], byte[]> Zero(int offset, int length) => blob =>
    {
        blob.AsSpan(offset, length).Clear();
        return blob;
    };

    private static byte[] Encode(int startDepth, int targetDepth, in Stem targetPath, in ValueHash256 targetHash)
    {
        byte[] encoded = new byte[PbtNodeChain.EncodedLength];
        PbtNodeChain.Write(encoded, startDepth, targetDepth, targetPath, targetHash);
        return encoded;
    }

    /// <summary>The smallest group encoding, to check the format bytes really are disjoint.</summary>
    private static byte[] EncodeGroup()
    {
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        PbtTrieNodeGroup.Builder builder = new(encoded);
        builder.AppendInternal(PbtTrieNodeGroup.RootPosition, TargetHash);
        return encoded[..builder.Finish()];
    }

    private static ValueHash256 ReferenceRoot(ReadOnlySpan<(byte[] Key, byte[] Value)> entries)
    {
        EipReferenceTree reference = new();
        foreach ((byte[] key, byte[] value) in entries) reference.Insert(key, value);
        return new ValueHash256(reference.Merkelize());
    }
}
