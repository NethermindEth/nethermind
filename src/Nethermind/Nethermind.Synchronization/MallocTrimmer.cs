// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Synchronization;

public class MallocTrimmer
{
    private MallocHelper _mallocHelper;
    private ILogger _logger;

    public MallocTrimmer(
        ISyncModeSelector syncModeSelector,
        TimeSpan interval,
        ILogManager logManager,
        MallocHelper? mallocHelper = null
    )
    {
        _mallocHelper = mallocHelper ?? new MallocHelper();
        _logger = logManager.GetClassLogger();

        Timer timer = new(interval);
        timer.Elapsed += (sender, args) => Trim();

        syncModeSelector.Changed += (_, args) =>
        {
            bool notSyncing = args.Current is SyncMode.None or SyncMode.WaitingForBlock;
            timer.Enabled = !notSyncing;
        };
    }

    private void Trim()
    {
        // Unlike a GC, this seems to be single threaded, going through each arena one by one.
        // It does however, lock the arena. On 32 thread machine, 256 arena, this take between 300ms to 1 second
        // in total, so about 1 to 4 ms for each arena, which is the possible hang when allocating via malloc.
        // This does not apply to managed code though, so this is largely the concern of rocksdb when reading
        // from uncached block.
        //
        // Unlike the standard free(), in addition to always clearing fastbin and consolidating chunks, this
        // also MADV_DONTNEED large enough free section of the heap. This also means private/virtual memory
        // does not go down, but RSS and GC load does.
        long startTime = Stopwatch.GetTimestamp();
        if (_logger.IsDebug) _logger.Debug("Trimming malloc heaps");
        bool wasReleased = _mallocHelper.MallocTrim((uint)1.MiB());
        if (_logger.IsDebug) _logger.Debug($"Trimming malloc heap took {Stopwatch.GetElapsedTime(startTime)}. wasReleased: {wasReleased}");
    }
}
