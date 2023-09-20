// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Logging;

namespace Nethermind.Core;

public class MallocTrimmer
{
    private MallocHelper _mallocHelper;
    private ILogger _logger;
    private TimeSpan _delay;

    public MallocTrimmer(TimeSpan delay, ILogManager logManager, MallocHelper? mallocHelper = null)
    {
        _mallocHelper = mallocHelper ?? new MallocHelper();
        _logger = logManager.GetClassLogger();
        _delay = delay;
    }

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
                bool wasReleased = _mallocHelper.MallocTrim((uint)1.MiB());
                if (_logger.IsDebug) _logger.Debug($"Trimming malloc heap took {Stopwatch.GetElapsedTime(startTime)}. wasReleased: {wasReleased}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
