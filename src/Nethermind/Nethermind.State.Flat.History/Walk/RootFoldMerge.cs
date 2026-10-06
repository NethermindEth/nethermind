// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

/// <summary>Merges root fold chunk findings into the walk's sink in block order.</summary>
/// <remarks>
/// Root fold chunks finish in any order; taking their findings in block order and nothing after the first chunk whose
/// header check stopped gives the findings and compared count of one fold over the whole range.
/// </remarks>
internal sealed class RootFoldMerge(MismatchSink sink, int chunks)
{
    private readonly ChunkOutcome?[] _completed = new ChunkOutcome?[chunks];
    private int _next;
    private bool _stopped;
    private int _firstStopped = int.MaxValue;

    public ulong Compared { get; private set; }

    public int RemainingCapacity
    {
        get
        {
            lock (_completed)
            {
                return _stopped ? 0 : Math.Max(0, MismatchSink.MaxRecorded - sink.Count);
            }
        }
    }

    /// <summary>Whether a chunk before <paramref name="chunk"/> stopped its header check, which discards that chunk's findings.</summary>
    public bool StoppedBefore(int chunk) => Volatile.Read(ref _firstStopped) < chunk;

    public void Complete(int chunk, MismatchSink found, ulong compared, bool stopped)
    {
        lock (_completed)
        {
            _completed[chunk] = new ChunkOutcome(found, compared, stopped);
            if (stopped && chunk < _firstStopped) Volatile.Write(ref _firstStopped, chunk);
            while (!_stopped && _next < _completed.Length && _completed[_next] is { } outcome)
            {
                _completed[_next++] = null;
                sink.AddRange(outcome.Found);
                Compared += outcome.Compared;
                _stopped = outcome.Stopped;
            }
        }
    }

    private readonly record struct ChunkOutcome(MismatchSink Found, ulong Compared, bool Stopped);
}
