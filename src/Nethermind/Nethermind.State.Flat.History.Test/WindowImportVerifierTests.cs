// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class WindowImportVerifierTests
{
    private static readonly ValueHash256 Seed = ValueKeccak.Compute("seed"u8);

    [Test]
    public async Task VerifyAsync_ForACleanDigestList_ReturnsVerifiedWithoutBisecting()
    {
        List<BlockDigest> digests = BuildDigests(1, 10);
        RecordingHashSource hashSource = RecordingHashSource.MatchingTheTrueFold(digests, Seed);

        WindowImportVerifier verifier = new();
        WindowImportVerdict verdict = await verifier.VerifyAsync(digests, Seed, fromBlockInclusive: 1, hashSource, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.True, "a digest list matching the honestly-computed claim must verify");
            Assert.That(hashSource.QueriedBlocks, Has.Count.EqualTo(1), "the happy path must query the claim exactly once (the batch's last block), never per intermediate block");
        }
    }

    [Test]
    public async Task VerifyAsync_WithASingleCorruptedDigest_BisectsToExactlyThatBlock()
    {
        List<BlockDigest> digests = BuildDigests(1, 20);
        RecordingHashSource hashSource = RecordingHashSource.MatchingTheTrueFold(digests, Seed);

        List<BlockDigest> corrupted = CorruptBlock(digests, corruptedBlock: 7);

        WindowImportVerifier verifier = new();
        WindowImportVerdict verdict = await verifier.VerifyAsync(corrupted, Seed, fromBlockInclusive: 1, hashSource, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            // Exact equality, not a wide <=/>= bound: this is what actually pins the bisection loop — deleting the
            // bisection loop entirely (reporting the whole [1,20] range as "isolated") would fail this assertion.
            Assert.That(verdict.Verified, Is.False, "a single corrupted digest must be reported as a mismatch");
            Assert.That(verdict.MismatchRangeStart, Is.EqualTo(7UL), "bisection must isolate exactly the corrupted block, not the whole range");
            Assert.That(verdict.MismatchRangeEnd, Is.EqualTo(7UL), "bisection must isolate exactly the corrupted block, not the whole range");
        }
    }

    [Test]
    public async Task VerifyAsync_WithCorruptionAtTheFirstBlock_BisectsToExactlyThatBlock()
    {
        List<BlockDigest> digests = BuildDigests(100, 115);
        RecordingHashSource hashSource = RecordingHashSource.MatchingTheTrueFold(digests, Seed);
        List<BlockDigest> corrupted = CorruptBlock(digests, corruptedBlock: 100);

        WindowImportVerifier verifier = new();
        WindowImportVerdict verdict = await verifier.VerifyAsync(corrupted, Seed, fromBlockInclusive: 100, hashSource, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(verdict.MismatchRangeStart, Is.EqualTo(100UL));
            Assert.That(verdict.MismatchRangeEnd, Is.EqualTo(100UL));
        }
    }

    [Test]
    public async Task VerifyAsync_WithCorruptionAtTheLastBlock_BisectsToExactlyThatBlock()
    {
        List<BlockDigest> digests = BuildDigests(1, 16);
        RecordingHashSource hashSource = RecordingHashSource.MatchingTheTrueFold(digests, Seed);
        List<BlockDigest> corrupted = CorruptBlock(digests, corruptedBlock: 16);

        WindowImportVerifier verifier = new();
        WindowImportVerdict verdict = await verifier.VerifyAsync(corrupted, Seed, fromBlockInclusive: 1, hashSource, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(verdict.MismatchRangeStart, Is.EqualTo(16UL));
            Assert.That(verdict.MismatchRangeEnd, Is.EqualTo(16UL));
        }
    }

    [Test]
    public void FoldAscending_IsOrderSensitive_DifferentSeedsProduceDifferentChains()
    {
        List<BlockDigest> digests = BuildDigests(1, 5);

        ValueHash256 chainFromSeedA = WindowImportVerifier.FoldAscending(digests, ValueKeccak.Compute("a"u8));
        ValueHash256 chainFromSeedB = WindowImportVerifier.FoldAscending(digests, ValueKeccak.Compute("b"u8));

        Assert.That(chainFromSeedA, Is.Not.EqualTo(chainFromSeedB), "the fold must depend on the seed, not just the digests");
    }

    private static List<BlockDigest> BuildDigests(ulong fromInclusive, ulong toInclusive)
    {
        List<BlockDigest> digests = new((int)(toInclusive - fromInclusive + 1));
        for (ulong block = fromInclusive; block <= toInclusive; block++)
        {
            digests.Add(new BlockDigest(block, ValueKeccak.Compute(BitConverter.GetBytes(block))));
        }

        return digests;
    }

    private static List<BlockDigest> CorruptBlock(List<BlockDigest> digests, ulong corruptedBlock)
    {
        List<BlockDigest> copy = new(digests.Count);
        foreach (BlockDigest entry in digests)
        {
            copy.Add(entry.Block == corruptedBlock ? entry with { Digest = ValueKeccak.Compute("corrupt"u8) } : entry);
        }

        return copy;
    }

    private sealed class RecordingHashSource : IChangesetHashSource
    {
        private readonly List<BlockDigest> _trueDigests;
        private readonly ValueHash256 _seed;

        public List<ulong> QueriedBlocks { get; } = [];

        private RecordingHashSource(List<BlockDigest> trueDigests, ValueHash256 seed)
        {
            _trueDigests = trueDigests;
            _seed = seed;
        }

        public static RecordingHashSource MatchingTheTrueFold(List<BlockDigest> trueDigests, ValueHash256 seed) => new(trueDigests, seed);

        public ValueTask<ValueHash256> GetClaimedChainHashAsync(ulong block, CancellationToken cancellationToken)
        {
            QueriedBlocks.Add(block);
            int endIndexInclusive = _trueDigests.FindIndex(d => d.Block == block);
            List<BlockDigest> prefix = _trueDigests.GetRange(0, endIndexInclusive + 1);
            return new ValueTask<ValueHash256>(WindowImportVerifier.FoldAscending(prefix, _seed));
        }
    }
}
