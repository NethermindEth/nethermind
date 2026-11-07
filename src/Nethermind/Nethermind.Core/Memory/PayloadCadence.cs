// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Memory;

/// <summary>Tracks how eligible payloads arrive: the interval between their starts and the idle gap each one leaves.</summary>
/// <remarks>
/// The idle gap runs from the moment the last payload in processing lets go, where the keeper schedules its post-block
/// collection, to the next payload's start; a payload that starts while another is still in processing leaves none.
/// Both are medians of the last <see cref="Samples"/> samples, or -1 until there are that many. Not thread-safe: the
/// keeper calls it under its lock.
/// </remarks>
internal sealed class PayloadCadence
{
    internal const int Samples = 16;
    /// <summary>Longer samples are left out: a node back from sync, or a paused CL, says nothing about the chain's cadence.</summary>
    internal const long MaxSampleMs = 60_000;

    private readonly SampleWindow _intervals = new();
    private readonly SampleWindow _idleGaps = new();
    private readonly long[] _sorted = new long[Samples];
    private long? _lastStartMs;
    private long? _lastIdleSinceMs;

    /// <summary>The median interval between payload starts, in milliseconds; -1 until there are enough samples.</summary>
    public long MedianIntervalMs { get; private set; } = -1;

    /// <summary>The median idle gap before a payload starts, in milliseconds; -1 until there are enough samples.</summary>
    public long MedianIdleGapMs { get; private set; } = -1;

    /// <param name="nowMs">The payload's start.</param>
    /// <param name="idle">Whether no other payload is in processing.</param>
    public void OnPayloadStart(long nowMs, bool idle)
    {
        if (_lastStartMs is long lastStartMs)
        {
            if (Add(_intervals, nowMs - lastStartMs)) MedianIntervalMs = Median(_intervals);

            long? idleGapMs = !idle ? 0 : _lastIdleSinceMs is long idleSinceMs ? nowMs - idleSinceMs : null;
            if (idleGapMs is long gapMs && Add(_idleGaps, gapMs)) MedianIdleGapMs = Median(_idleGaps);
        }

        _lastStartMs = nowMs;
        _lastIdleSinceMs = null;
    }

    /// <summary>Marks the moment the last payload in processing let go.</summary>
    public void OnIdle(long nowMs) => _lastIdleSinceMs = nowMs;

    private static bool Add(SampleWindow window, long sampleMs)
    {
        if (sampleMs < 0 || sampleMs > MaxSampleMs) return false;
        window.Values[window.Next] = sampleMs;
        window.Next = (window.Next + 1) % Samples;
        window.Count = Math.Min(window.Count + 1, Samples);
        return true;
    }

    private long Median(SampleWindow window)
    {
        if (window.Count < Samples) return -1;
        Array.Copy(window.Values, _sorted, Samples);
        Array.Sort(_sorted);
        return (_sorted[Samples / 2 - 1] + _sorted[Samples / 2]) / 2;
    }

    private sealed class SampleWindow
    {
        public readonly long[] Values = new long[Samples];
        public int Next;
        public int Count;
    }
}
