// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History;

public enum HeightVerificationStatus
{
    Verified,
    Mismatch,
    CannotEvaluate,
}

public readonly record struct SampledHeightVerdict(ulong Block, HeightVerificationStatus Status);

public readonly record struct ArchiveCloneVerdict(bool Verified, IReadOnlyList<SampledHeightVerdict> Samples);

public sealed class ArchiveCloneVerifier
{
    public const int MaxBisectionDepth = 32;

    private readonly HistoryAvailability _availability;
    private readonly ICloneHeaderSource _headers;

    public ArchiveCloneVerifier(HistoryAvailability availability, ICloneHeaderSource headers, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(logManager);
        _availability = availability;
        _headers = headers;
    }

    public static ulong[] LogSpacedHeights(ulong floorInclusive, ulong watermarkInclusive, int sampleCount = 8)
    {
        if (sampleCount <= 0 || watermarkInclusive <= floorInclusive) return [];

        ulong span = watermarkInclusive - floorInclusive;
        SortedSet<ulong> heights = [];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = sampleCount == 1 ? 1.0 : (double)i / (sampleCount - 1);
            double weighted = 1.0 - Math.Pow(1.0 - t, 2.0);
            ulong offset = (ulong)(weighted * span);
            heights.Add(floorInclusive + offset);
        }

        return [.. heights];
    }

    public SampledHeightVerdict VerifyHeight(ulong block)
    {
        ValueHash256? expectedRoot = _headers.TryGetStateRoot(block);
        if (expectedRoot is null) return new SampledHeightVerdict(block, HeightVerificationStatus.CannotEvaluate);

        bool markerMatches = _availability.IsCoveredAndRootMatches(block, expectedRoot.Value);
        return new SampledHeightVerdict(block, markerMatches ? HeightVerificationStatus.Verified : HeightVerificationStatus.Mismatch);
    }

    public ArchiveCloneVerdict VerifySampledHeights(int sampleCount)
    {
        if (!_availability.TryGetWatermark(out ulong watermark)) return new ArchiveCloneVerdict(false, []);
        _availability.TryGetGlobalFloor(out ulong floor);

        ulong[] heights = LogSpacedHeights(floor, watermark, sampleCount);
        if (heights.Length == 0) return new ArchiveCloneVerdict(false, []);

        List<SampledHeightVerdict> results = new(heights.Length);
        bool anyMismatch = false;
        bool anyVerified = false;
        foreach (ulong height in heights)
        {
            SampledHeightVerdict verdict = VerifyHeight(height);
            results.Add(verdict);
            if (verdict.Status == HeightVerificationStatus.Mismatch) anyMismatch = true;
            if (verdict.Status == HeightVerificationStatus.Verified) anyVerified = true;
        }

        return new ArchiveCloneVerdict(anyVerified && !anyMismatch, results);
    }

    public ulong Bisect(ulong floorInclusive, ulong watermarkInclusive, Func<ulong, bool> isOkOrUnresolvable, CancellationToken cancellationToken)
    {
        ulong low = floorInclusive;
        ulong high = watermarkInclusive;

        for (int depth = 0; depth < MaxBisectionDepth && low < high; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong mid = low + (high - low) / 2;
            if (isOkOrUnresolvable(mid)) low = mid + 1;
            else high = mid;
        }

        return high;
    }
}
