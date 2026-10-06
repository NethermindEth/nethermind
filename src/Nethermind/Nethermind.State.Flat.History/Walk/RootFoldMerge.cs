// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

/// <remarks>
/// Root fold chunks finish in any order; taking their findings in block order and nothing after the first chunk whose
/// header check stopped gives the findings and compared count of one fold over the whole range.
/// </remarks>
internal sealed class RootFoldMerge(MismatchSink sink, int chunks)
{
    private readonly ChunkOutcome?[] _completed = new ChunkOutcome?[chunks];
    private int _next;
    private bool _stopped;

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

    public void Complete(int chunk, MismatchSink found, ulong compared, bool stopped)
    {
        lock (_completed)
        {
            _completed[chunk] = new ChunkOutcome(found, compared, stopped);
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
