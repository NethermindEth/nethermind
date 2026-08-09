// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ArchiveCloneVerifierTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp() => _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    private ArchiveCloneVerifier CreateVerifier(ICloneHeaderSource headers) =>
        new(new HistoryAvailability(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)), headers, LimboLogs.Instance);

    [Test]
    public void Bisect_FindsLowestFailingHeight()
    {
        ArchiveCloneVerifier verifier = CreateVerifier(new FakeHeaderSource());

        ulong isolated = verifier.Bisect(0, 100, h => h < 42, CancellationToken.None);

        Assert.That(isolated, Is.EqualTo(42UL), "the bisection must converge on the exact point where isOkOrUnresolvable first turns false");
    }

    [Test]
    public void VerifySampledHeights_WhenNoWatermarkPublished_ReturnsUnverified()
    {
        ArchiveCloneVerifier verifier = CreateVerifier(new FakeHeaderSource());

        Assert.That(verifier.VerifySampledHeights(8).Verified, Is.False);
    }

    [Test]
    public void VerifySampledHeights_ZeroSampleCount_ReturnsUnverified()
    {
        HistoryColumnsWriter.SetWatermark(_historyColumns, 100);
        ArchiveCloneVerifier verifier = CreateVerifier(new FakeHeaderSource());

        Assert.That(verifier.VerifySampledHeights(0).Verified, Is.False, "zero samples must never be reported as verified");
    }

    [Test]
    public void VerifySampledHeights_AllCorrect_ReportsVerified()
    {
        HistoryColumnsWriter.MarkBlock(_historyColumns, 5, ValueKeccak.Compute("root"u8));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 5);

        FakeHeaderSource headers = new();
        headers.Roots[5] = ValueKeccak.Compute("root"u8);

        ArchiveCloneVerifier verifier = CreateVerifier(headers);
        ArchiveCloneVerdict verdict = verifier.VerifySampledHeights(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.True);
            Assert.That(verdict.Samples[0].Status, Is.EqualTo(HeightVerificationStatus.Verified));
        }
    }

    [Test]
    public void VerifySampledHeights_MarkerMismatch_ReportsMismatchAndUnverified()
    {
        HistoryColumnsWriter.MarkBlock(_historyColumns, 5, ValueKeccak.Compute("wrong"u8));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 5);

        FakeHeaderSource headers = new();
        headers.Roots[5] = ValueKeccak.Compute("real"u8);

        ArchiveCloneVerifier verifier = CreateVerifier(headers);
        ArchiveCloneVerdict verdict = verifier.VerifySampledHeights(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(verdict.Samples[0].Status, Is.EqualTo(HeightVerificationStatus.Mismatch));
        }
    }

    [Test]
    public void VerifySampledHeights_UnresolvableHeader_ReportsCannotEvaluate_NeverMismatch_AndUnverifiedOverall()
    {
        HistoryColumnsWriter.MarkBlock(_historyColumns, 5, ValueKeccak.Compute("root"u8));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 5);

        ArchiveCloneVerifier verifier = CreateVerifier(new FakeHeaderSource());
        ArchiveCloneVerdict verdict = verifier.VerifySampledHeights(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Samples[0].Status, Is.EqualTo(HeightVerificationStatus.CannotEvaluate),
                "a locally-unresolvable header is neither confirmed nor a mismatch - it is simply unevaluated");
            Assert.That(verdict.Verified, Is.False, "nothing was actually confirmed, so the overall verdict must not report Verified=true");
        }
    }

    [Test]
    public void LogSpacedHeights_IsTipWeighted_DenserNearTheWatermark()
    {
        ulong[] heights = ArchiveCloneVerifier.LogSpacedHeights(0, 100, 5);

        List<ulong> gaps = [];
        for (int i = 1; i < heights.Length; i++) gaps.Add(heights[i] - heights[i - 1]);

        for (int i = 1; i < gaps.Count; i++)
        {
            Assert.That(gaps[i], Is.LessThanOrEqualTo(gaps[i - 1]),
                "samples must get denser (smaller gaps) approaching the watermark, not the floor - tip-weighted is 1-(1-t)^2, not t^2");
        }
    }

    [Test]
    public void LogSpacedHeights_ReturnsAscendingUniqueSamplesWithinFloorAndWatermarkInclusive()
    {
        ulong[] heights = ArchiveCloneVerifier.LogSpacedHeights(10, 110, 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heights, Is.Ordered);
            Assert.That(heights, Is.Unique);
            Assert.That(heights[0], Is.GreaterThanOrEqualTo(10UL));
            Assert.That(heights[^1], Is.EqualTo(110UL));
        }
    }

    [Test]
    public void LogSpacedHeights_WhenWatermarkNotAboveFloor_ReturnsEmpty() =>
        Assert.That(ArchiveCloneVerifier.LogSpacedHeights(50, 50, 8), Is.Empty);

    private sealed class FakeHeaderSource : ICloneHeaderSource
    {
        public Dictionary<ulong, ValueHash256> Roots { get; } = [];

        public ValueHash256? TryGetStateRoot(ulong block)
        {
            if (Roots.TryGetValue(block, out ValueHash256 root)) return root;
            return null;
        }
    }
}
