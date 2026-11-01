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
public sealed class PrestateRecorder(
    PrestateRecordStore store,
    HistoryAvailability availability,
    IFlatDbConfig config,
    ILogManager logManager,
    PrestateRecordCache? cache = null)
{
    internal const ulong PruneIntervalBlocks = 1024;
    private static readonly TimeSpan RepeatedFailureInterval = TimeSpan.FromMinutes(1);

    private readonly int _maxRecordBytes = Math.Max(PrestateRecordFormat.HeaderLength, config.HistoryPrestateRecordsMaxRecordBytes);
    private readonly ulong _retentionBlocks = config.HistoryPrestateRecordsRetentionBlocks;
    private readonly ILogger _logger = logManager.GetClassLogger<PrestateRecorder>();
    private readonly Lock _state = new();
    private ulong _prunedAt;
    private long _writeFailureReportedAt;
    private long _captureFailureReportedAt;
    private long _pruneFailureReportedAt;

    /// <summary>A capture for one executor; it records only while armed.</summary>
    public PrestateCapture CreateCapture() => new();

    /// <summary>Stores the record of <paramref name="block"/> from a capture of its execution on its parent, then
    /// prunes when due. False when the capture is unusable, the record is too large or the write failed.</summary>
    public bool TryWrite(PrestateCapture capture, ulong block, in ValueHash256 blockHash, CancellationToken cancellationToken)
    {
        if (!capture.IsComplete || capture.Parent.BlockNumber + 1 != block)
        {
            SkipCapture(block, capture);
            return false;
        }

        if (!TryWriteRecord(capture, block, blockHash)) return false;

        Prune(block, cancellationToken);
        return true;
    }

    /// <remarks>On a failure the rented buffer is left to the collector rather than returned.</remarks>
    private bool TryWriteRecord(PrestateCapture capture, ulong block, in ValueHash256 blockHash)
    {
        try
        {
            if (!capture.TryEncode(block, blockHash, _maxRecordBytes, out byte[]? buffer, out int length))
            {
                Skip(block, $"the record exceeds {_maxRecordBytes} bytes");
                return false;
            }

            store.Write(capture.Parent, buffer.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(buffer);
            cache?.Invalidate(capture.Parent);
            Metrics.RecordPrestateRecordWritten();
            return true;
        }
        catch (Exception exception)
        {
            Metrics.RecordPrestateRecordSkipped();
            if (_logger.IsWarn && ShouldReport(ref _writeFailureReportedAt)) _logger.Warn($"Pre-state record of block {block} was not written: {exception.Message}");
            return false;
        }
    }

    /// <summary>Every <see cref="PruneIntervalBlocks"/> blocks, drops records below the window and below the history
    /// floor, where no state is readable any more. Space is reclaimed only while no shutdown is pending. A failed prune
    /// is retried at the next interval and does not affect the record just written.</summary>
    internal void Prune(ulong block, CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_prunedAt != 0 && block < _prunedAt + PruneIntervalBlocks) return;
            _prunedAt = block;
        }

        try
        {
            store.PruneBelow(PruneFloor(), reclaim: !cancellationToken.IsCancellationRequested);
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn && ShouldReport(ref _pruneFailureReportedAt)) _logger.Warn($"Pre-state records could not be pruned, retrying later: {exception.Message}");
        }
    }

    /// <summary>The lowest parent block whose record is kept: one above the history floor, raised to
    /// <c>watermark - retention</c> when a window is configured.</summary>
    internal ulong PruneFloor()
    {
        ulong floor = availability.TryGetGlobalFloor(out ulong historyFloor) ? historyFloor + 1 : 0;
        if (_retentionBlocks != 0 && availability.TryGetWatermark(out ulong watermark) && watermark > _retentionBlocks)
            floor = Math.Max(floor, watermark - _retentionBlocks);
        return floor;
    }

    private void SkipCapture(ulong block, PrestateCapture capture)
    {
        Metrics.RecordPrestateRecordSkipped();
        string reason = capture.Failure ?? "the captured scope is not the parent of the block";
        if (capture.FailedUnexpectedly && _logger.IsWarn && ShouldReport(ref _captureFailureReportedAt))
            _logger.Warn($"Pre-state record of block {block} was not captured ({reason}); historical traces of such blocks read flat history.");
        else if (_logger.IsDebug) _logger.Debug($"Pre-state record of block {block} was not written: {reason}");
    }

    private void Skip(ulong block, string reason)
    {
        Metrics.RecordPrestateRecordSkipped();
        if (_logger.IsDebug) _logger.Debug($"Pre-state record of block {block} was not written: {reason}");
    }

    private bool ShouldReport(ref long reportedAt)
    {
        lock (_state)
        {
            long now = Stopwatch.GetTimestamp();
            if (reportedAt != 0 && Stopwatch.GetElapsedTime(reportedAt, now) < RepeatedFailureInterval) return false;
            reportedAt = now;
            return true;
        }
    }
}
