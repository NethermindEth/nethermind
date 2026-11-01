// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>Turns the captures of the transaction changeset builder into stored records and keeps the store inside
/// its retention window. Runs only on the builder's threads, behind the history watermark, never on block processing.</summary>
/// <remarks>A record is derived data: a block that is not recorded is traced from flat history as before, so no failure
/// here may fail the changeset build that carries it.</remarks>
public sealed class PrestateRecorder(PrestateRecordStore store, HistoryAvailability availability, IFlatDbConfig config, ILogManager logManager)
{
    internal const ulong PruneIntervalBlocks = 1024;
    private static readonly TimeSpan RepeatedFailureInterval = TimeSpan.FromMinutes(1);

    private readonly int _maxRecordBytes = Math.Max(PrestateRecordFormat.HeaderLength, config.HistoryPrestateRecordsMaxRecordBytes);
    private readonly ulong _retentionBlocks = config.HistoryPrestateRecordsRetentionBlocks;
    private readonly ILogger _logger = logManager.GetClassLogger<PrestateRecorder>();
    private readonly Lock _state = new();
    private ulong _prunedAt;
    private long _failureReportedAt;

    /// <summary>A capture for one executor; it records only between <see cref="PrestateCapture.Arm"/> and its reset.</summary>
    public PrestateCapture CreateCapture() => new();

    /// <summary>Stores the record of <paramref name="block"/> from a capture of its execution on its parent. False
    /// when the capture is unusable, the record is too large or the write failed.</summary>
    public bool TryWrite(PrestateCapture capture, ulong block, in ValueHash256 blockHash, CancellationToken cancellationToken)
    {
        if (!capture.IsComplete || capture.Parent.BlockNumber + 1 != block)
        {
            Skip(block, capture.Failure ?? "the captured scope is not the parent of the block");
            return false;
        }

        byte[]? buffer = null;
        try
        {
            if (!capture.TryEncode(block, blockHash, _maxRecordBytes, out buffer, out int length))
            {
                Skip(block, $"the record exceeds {_maxRecordBytes} bytes");
                return false;
            }

            store.Write(capture.Parent, buffer.AsSpan(0, length));
            Metrics.PrestateRecordsWritten++;
            Prune(block, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            Skip(block, exception.Message, warn: true);
            return false;
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Every <see cref="PruneIntervalBlocks"/> blocks, drops records below the window and below the history
    /// floor, where no state is readable any more. Space is reclaimed only while no shutdown is pending.</summary>
    internal void Prune(ulong block, CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_prunedAt != 0 && block < _prunedAt + PruneIntervalBlocks) return;
            _prunedAt = block;
        }

        ulong floor = availability.TryGetGlobalFloor(out ulong historyFloor) ? historyFloor + 1 : 0;
        if (_retentionBlocks != 0 && availability.TryGetWatermark(out ulong watermark) && watermark > _retentionBlocks)
            floor = Math.Max(floor, watermark - _retentionBlocks);

        store.PruneBelow(floor, reclaim: !cancellationToken.IsCancellationRequested);
    }

    private void Skip(ulong block, string reason, bool warn = false)
    {
        Metrics.PrestateRecordsSkipped++;
        if (warn && _logger.IsWarn && ShouldReport()) _logger.Warn($"Pre-state record of block {block} was not written: {reason}");
        else if (_logger.IsDebug) _logger.Debug($"Pre-state record of block {block} was not written: {reason}");
    }

    private bool ShouldReport()
    {
        lock (_state)
        {
            long now = Stopwatch.GetTimestamp();
            if (_failureReportedAt != 0 && Stopwatch.GetElapsedTime(_failureReportedAt, now) < RepeatedFailureInterval) return false;
            _failureReportedAt = now;
            return true;
        }
    }
}
