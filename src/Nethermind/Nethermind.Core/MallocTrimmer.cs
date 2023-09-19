// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Core;

public class MallocTrimmer
{
    private ILogger _logger;
    private TimeSpan _delay;

    public MallocTrimmer(TimeSpan delay, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _delay = delay;
    }

    [DllImport("libc")]
    private static extern int malloc_trim(UIntPtr trailingSpace);

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_delay, cancellationToken);

                // Unlike a GC, this seems to be single threaded, going through each arena one by one.
                // It does however, lock the arena.
                long startTime = Stopwatch.GetTimestamp();
                if (_logger.IsDebug) _logger.Debug("Trimming malloc heaps");
                bool wasReleased = malloc_trim((uint)1.MiB()) == 1;
                if (_logger.IsDebug) _logger.Debug($"Trimming malloc heap took {Stopwatch.GetElapsedTime(startTime)}. wasReleased: {wasReleased}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
