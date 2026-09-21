// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.State.Flat;

public sealed class SweepPacer
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private long _resumeAt;

    public bool Run(Func<bool> pass, CancellationToken token)
    {
        _turn.Wait(token);
        try
        {
            TimeSpan owed = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _resumeAt);
            if (owed > TimeSpan.Zero && token.WaitHandle.WaitOne(owed)) throw new OperationCanceledException(token);

            long startedAt = Stopwatch.GetTimestamp();
            bool completed = pass();
            long finishedAt = Stopwatch.GetTimestamp();
            _resumeAt = finishedAt + (finishedAt - startedAt);
            return completed;
        }
        finally
        {
            _turn.Release();
        }
    }
}
