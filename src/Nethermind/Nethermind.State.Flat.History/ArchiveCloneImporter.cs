// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.State.Flat.History;

public sealed class ArchiveCloneImporter
{
    private const int BlockBytes = sizeof(ulong);
    private const int MaxConcurrentStreams = IHistoryServer.MaxInFlightRequestsPerPeer - 1;
    private const int VerificationSampleCount = 8;
    private const int TimeoutRetryLimit = 5;
    private const int RefusedRetryLimit = 90;
    private static readonly TimeSpan RefusedRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(30);

    // AvailableBlocks last: the watermark this importer publishes at the end depends on every other column
    // already being durably cloned, so a crash before that publish leaves every local read reporting
    // "nothing covered" instead of resolving against a partially-cloned AccountHistory/StorageHistory.
    private static readonly HistoryRowColumn[] ColumnsInCloneOrder =
    [
        HistoryRowColumn.Code,
        HistoryRowColumn.AccountHistory,
        HistoryRowColumn.StorageHistory,
        HistoryRowColumn.StorageClears,
        HistoryRowColumn.AvailableBlocks,
    ];

    private static ReadOnlySpan<byte> ShardCursorKeyPrefix => "archiveclone:cursor:"u8;
    private static ReadOnlySpan<byte> ColumnDoneKeyPrefix => "archiveclone:done:"u8;
    private static ReadOnlySpan<byte> TargetWatermarkKey => "archiveclone:watermark"u8;

    private readonly IArchiveCloneSource _source;
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IDb _code;
    private readonly IDb _metadata;
    private readonly HistoryWindowPruner _pruner;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly ArchiveCloneVerifier _verifier;
    private readonly int _shardCount;
    private readonly int _shardBufferBudget;
    private readonly int _streamCount;
    private readonly ILogger _logger;

    private long _columnBytes;
    private long _columnRows;
    private int _columnShardsDone;
    private long _columnStartTimestamp;
    private long _lastProgressLogTimestamp;
    private double[] _shardProgress = [];
    private ulong _progressTargetWatermark;

    public ArchiveCloneImporter(
        IArchiveCloneSource source,
        IColumnsDb<FlatHistoryColumns> history,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IFlatDbConfig config,
        HistoryWindowPruner pruner,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        ArchiveCloneVerifier verifier,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(codeDb);
        ArgumentNullException.ThrowIfNull(metadataDb);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
        _source = source;
        _history = history;
        _code = codeDb;
        _metadata = metadataDb;
        _pruner = pruner;
        _availability = availability;
        _rowFormat = rowFormat;
        _shardCount = Math.Max(1, config.HistoryImportShardCount);
        _shardBufferBudget = Math.Max(1, config.HistoryImportShardBufferBudgetEntries);
        _streamCount = Math.Clamp(config.HistoryCloneStreamCount, 1, Math.Min(MaxConcurrentStreams, _shardCount));
        _logger = logManager.GetClassLogger<ArchiveCloneImporter>();
    }

    public async Task<ulong> CloneAsync(CancellationToken cancellationToken)
    {
        if (!_source.SupportsFullClone)
        {
            throw new InvalidConfigurationException(
                "The configured clone source cannot serve a full archive clone; refusing rather than silently importing an incomplete history.", -1);
        }

        if (_source.RowFormatVersion != _rowFormat.FormatVersion)
        {
            throw new InvalidConfigurationException(
                $"The configured clone source carries row format {_source.RowFormatVersion}, but this node has resolved format {_rowFormat.FormatVersion}; no transcoding is supported.", -1);
        }

        ulong targetWatermark = ReadOrStoreTargetWatermark();
        Volatile.Write(ref _progressTargetWatermark, targetWatermark);

        foreach (HistoryRowColumn column in ColumnsInCloneOrder)
        {
            await CloneColumnAsync(column, cancellationToken);
        }

        VerifyBeforePublish(targetWatermark);

        _availability.PublishWatermark(targetWatermark, _source.RowFormatVersion);
        return targetWatermark;
    }

    /// <summary>Discards the stored target watermark, done markers, and shard cursors so the next
    /// <see cref="CloneAsync"/> re-streams everything against the source's current watermark. Rows already
    /// imported stay (re-imports overwrite idempotently); the already-published watermark stays honest because
    /// the new pass only ever adds rows above it.</summary>
    public void ResetForNewTarget()
    {
        _metadata.Remove(TargetWatermarkKey);
        foreach (HistoryRowColumn column in ColumnsInCloneOrder)
        {
            _metadata.Remove(ColumnDoneKey(column));
            for (int shard = 0; shard < _shardCount; shard++)
            {
                ClearShardCursor(column, shard);
            }
        }

        _metadata.SyncWal();
    }

    /// <summary>Refuses to publish a range whose imported state roots disagree with the headers this node synced
    /// on its own. The source chose the rows it served but had no say in those headers, so a fabricated or shifted
    /// root index cannot satisfy both - and publishing an unchecked range would also hand it to the next consumer,
    /// since a node that finished a clone goes on to serve one. Costs a lookup per sample against hours of
    /// streaming, so it gates every clone rather than being an opt-in pass.</summary>
    private void VerifyBeforePublish(ulong targetWatermark)
    {
        _availability.TryGetGlobalFloor(out ulong floor);
        if (targetWatermark <= floor)
        {
            // Nothing was imported above the floor, so there is no root to disagree about.
            return;
        }

        ArchiveCloneVerdict verdict = _verifier.VerifyImportedRange(floor, targetWatermark, VerificationSampleCount);
        if (verdict.Verified)
        {
            int verified = 0;
            foreach (SampledHeightVerdict sample in verdict.Samples)
            {
                if (sample.Status == HeightVerificationStatus.Verified) verified++;
            }

            if (_logger.IsInfo) _logger.Info($"Archive clone: the imported state roots match this node's own headers at {verified} sampled heights.");
            return;
        }

        string detail = verdict.Samples.Count == 0
            ? "no height could be sampled"
            : string.Join(", ", verdict.Samples.Select(static s => $"{s.Block}:{s.Status}"));
        throw new InvalidOperationException(
            $"The cloned history disagrees with the headers this node synced independently, so it was not published ({detail}).");
    }

    /// <summary>The target watermark a started pass froze, if one is in progress. A resumed pass keeps streaming
    /// against this height even when it switches to a different source, so a replacement source whose own coverage
    /// ends below it cannot serve the rest of the pass and must not be selected.</summary>
    public static bool TryReadStoredTargetWatermark(IDb metadata, out ulong watermark)
    {
        byte[]? stored = metadata.Get(TargetWatermarkKey);
        if (stored is { Length: BlockBytes })
        {
            watermark = BinaryPrimitives.ReadUInt64BigEndian(stored);
            return true;
        }

        watermark = 0;
        return false;
    }

    private ulong ReadOrStoreTargetWatermark()
    {
        if (TryReadStoredTargetWatermark(_metadata, out ulong storedWatermark))
        {
            return storedWatermark;
        }

        ulong watermark = _source.Watermark;
        Span<byte> value = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, watermark);
        _metadata.PutSpan(TargetWatermarkKey, value);
        _metadata.SyncWal();
        return watermark;
    }

    public ArchiveCloneVerdict VerifyAndBan(ArchiveCloneVerifier verifier, int sampleCount, IArchiveClonePeerSink? peerSink)
    {
        ArchiveCloneVerdict verdict = verifier.VerifySampledHeights(sampleCount);
        if (verdict.Verified) return verdict;

        ulong firstMismatch = 0;
        bool anyMismatch = false;
        foreach (SampledHeightVerdict sample in verdict.Samples)
        {
            if (sample.Status != HeightVerificationStatus.Mismatch) continue;
            if (!anyMismatch || sample.Block < firstMismatch)
            {
                firstMismatch = sample.Block;
                anyMismatch = true;
            }
        }

        if (!anyMismatch) return verdict;

        _availability.TryGetGlobalFloor(out ulong floor);
        ulong isolated = verifier.Bisect(floor, firstMismatch, h => verifier.VerifyHeight(h).Status != HeightVerificationStatus.Mismatch, CancellationToken.None);

        if (_logger.IsWarn) _logger.Warn($"Full-archive clone verification failed near block {isolated}; banning the clone source.");

        if (peerSink is not null)
        {
            peerSink.BanSource(_source, $"sampled clone verification mismatch isolated near block {isolated}");
            if (peerSink.TryGetAlternateSource(_source, out IArchiveCloneSource? alternate) && _logger.IsWarn)
            {
                _logger.Warn($"An alternate clone source ({alternate}) is available; re-cloning the isolated range is not automated in this pass.");
            }
        }

        return verdict;
    }

    private async Task CloneColumnAsync(HistoryRowColumn column, CancellationToken cancellationToken)
    {
        if (_metadata.Get(ColumnDoneKey(column)) is not null)
        {
            if (_logger.IsInfo) _logger.Info($"Archive clone: {column} already cloned in a previous run, skipping.");
            return;
        }

        Volatile.Write(ref _columnBytes, 0);
        Volatile.Write(ref _columnRows, 0);
        Volatile.Write(ref _columnShardsDone, 0);
        Volatile.Write(ref _shardProgress, new double[_shardCount]);
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _columnStartTimestamp, now);
        Volatile.Write(ref _lastProgressLogTimestamp, now);
        if (_logger.IsInfo) _logger.Info($"Archive clone: streaming {column} ({_shardCount} shards, {_streamCount} concurrent streams).");

        IDb destination = ResolveColumn(column);
        (destination as ITunableDb)?.Tune(ITunableDb.TuneType.HeavyWrite);
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = linked.Token;
            Exception? failure = null;
            int nextShard = -1;
            Task[] workers = new Task[_streamCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            int shard = Interlocked.Increment(ref nextShard);
                            if (shard >= _shardCount) return;

                            (byte[] shardStart, byte[] shardEnd) = ShardBounds(shard, _shardCount);
                            using (await _pruner.BeginBackfillAsync(token))
                            {
                                await CloneShardAsync(column, destination, shard, shardStart, shardEnd, token);
                            }

                            SetShardProgress(shard, 1.0);
                            Interlocked.Increment(ref _columnShardsDone);
                        }
                    }
                    catch (Exception e)
                    {
                        if (e is not OperationCanceledException || !token.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.CompareExchange(ref failure, e, null);
                        }

                        try
                        {
                            linked.Cancel();
                        }
                        catch
                        {
                        }
                    }
                }, CancellationToken.None);
            }

            await Task.WhenAll(workers);

            cancellationToken.ThrowIfCancellationRequested();
            if (failure is OperationCanceledException timeout)
            {
                throw new InvalidOperationException($"A {column} clone stream was cancelled without a shutdown request; treating it as a retryable stream failure.", timeout);
            }

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            (destination as ITunableDb)?.Tune(ITunableDb.TuneType.Default);
        }

        _metadata.PutSpan(ColumnDoneKey(column), [1]);
        _metadata.SyncWal();
        for (int shard = 0; shard < _shardCount; shard++)
        {
            ClearShardCursor(column, shard);
        }

        if (_logger.IsInfo)
        {
            double seconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _columnStartTimestamp)).TotalSeconds;
            long bytes = Volatile.Read(ref _columnBytes);
            _logger.Info($"Archive clone: {column} complete - {Volatile.Read(ref _columnRows):N0} rows, {FormatMB(bytes)} fetched this run in {seconds / 60:F1} min ({FormatRate(bytes, seconds)}).");
        }
    }

    private static byte[] ColumnDoneKey(HistoryRowColumn column)
    {
        byte[] key = new byte[ColumnDoneKeyPrefix.Length + 1];
        ColumnDoneKeyPrefix.CopyTo(key);
        key[^1] = (byte)column;
        return key;
    }

    private void ReportPageProgress(HistoryRowColumn column, int pageRows, long pageBytes)
    {
        Interlocked.Add(ref _columnRows, pageRows);
        Interlocked.Add(ref _columnBytes, pageBytes);

        if (!_logger.IsInfo) return;

        long last = Volatile.Read(ref _lastProgressLogTimestamp);
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(last, now) < ProgressLogInterval) return;
        if (Interlocked.CompareExchange(ref _lastProgressLogTimestamp, now, last) != last) return;

        double seconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _columnStartTimestamp), now).TotalSeconds;
        long bytes = Volatile.Read(ref _columnBytes);
        double fraction = ColumnProgressFraction();
        _logger.Info($"Archive clone {column,-15} ({fraction * 100,6:F2} %) {Progress.GetMeter((float)fraction, 1)} {FormatMB(bytes)} | {Volatile.Read(ref _columnRows):N0} rows | {FormatRate(bytes, seconds)} | shards {Volatile.Read(ref _columnShardsDone)}/{_shardCount}");
    }

    private double ColumnProgressFraction()
    {
        double[] progress = Volatile.Read(ref _shardProgress);
        if (progress.Length == 0) return 0;

        double sum = 0;
        for (int i = 0; i < progress.Length; i++) sum += progress[i];
        return sum / progress.Length;
    }

    private void SetShardProgress(int shard, double fraction)
    {
        double[] progress = Volatile.Read(ref _shardProgress);
        if (shard < progress.Length) progress[shard] = fraction;
    }

    private double ShardFraction(HistoryRowColumn column, byte[] key, byte[] shardStart, byte[] shardEnd)
    {
        if (column == HistoryRowColumn.AvailableBlocks)
        {
            ulong target = Volatile.Read(ref _progressTargetWatermark);
            if (target == 0 || key.Length != BlockBytes) return 0;
            return Math.Clamp(BinaryPrimitives.ReadUInt64BigEndian(key) / (double)target, 0, 1);
        }

        uint position = ReadKeyPrefix(key);
        uint rangeStart = (uint)shardStart[0] << 24;
        uint rangeEndInclusive = ((uint)shardEnd[0] << 24) | 0x00FFFFFF;
        double span = (double)rangeEndInclusive - rangeStart + 1;
        return Math.Clamp((position - (double)rangeStart) / span, 0, 1);
    }

    private static uint ReadKeyPrefix(byte[] key)
    {
        uint value = 0;
        for (int i = 0; i < sizeof(uint); i++)
        {
            value = (value << 8) | (i < key.Length ? key[i] : (byte)0);
        }

        return value;
    }

    private static string FormatMB(long bytes) => $"{bytes / (1024.0 * 1024.0):N0} MB";

    private static string FormatRate(long bytes, double seconds) =>
        seconds > 0 ? $"{bytes / (1024.0 * 1024.0) / seconds:F1} MB/s" : "0.0 MB/s";

    private async Task CloneShardAsync(HistoryRowColumn column, IDb destination, int shard, byte[] shardStart, byte[] shardEnd, CancellationToken cancellationToken)
    {
        byte[]? resumeCursor = ReadShardCursor(column, shard);
        byte[] from = resumeCursor is null ? shardStart : RawRowKeys.NextKeyAfter(resumeCursor);
        if (resumeCursor is not null) SetShardProgress(shard, ShardFraction(column, resumeCursor, shardStart, shardEnd));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[]? lastWritten = null;
            int written = 0;
            byte[]? sourceCursor = null;
            bool exhausted = false;

            using (IWriteBatch batch = destination.StartWriteBatch())
            {
                while (true)
                {
                    ArchiveCloneRowPage page = await FetchPageAsync(column, shard, from, shardEnd, sourceCursor, cancellationToken);

                    int pageRows = 0;
                    long pageBytes = 0;
                    foreach (HistoryRowEntry entry in page.Entries)
                    {
                        if (column == HistoryRowColumn.AvailableBlocks && entry.Key.Length != BlockBytes) continue;
                        if (column == HistoryRowColumn.Code && !IsCodeRowAuthentic(entry))
                        {
                            throw new InvalidOperationException(
                                $"The clone source served a code row whose value does not hash to its key; the source is serving forged code and the import was abandoned.");
                        }

                        WriteRow(batch, entry);
                        lastWritten = entry.Key;
                        written++;
                        pageRows++;
                        pageBytes += entry.Key.Length + entry.Value.Length;
                    }

                    ReportPageProgress(column, pageRows, pageBytes);

                    if (page.NextCursor is null)
                    {
                        exhausted = true;
                        break;
                    }

                    sourceCursor = page.NextCursor;
                    if (written >= _shardBufferBudget) break;
                }
            }

            if (lastWritten is not null)
            {
                SyncDestination(column, destination);
                WriteShardCursor(column, shard, lastWritten);
                SetShardProgress(shard, ShardFraction(column, lastWritten, shardStart, shardEnd));
                from = RawRowKeys.NextKeyAfter(lastWritten);
            }

            if (exhausted) break;
        }
    }

    private async Task<ArchiveCloneRowPage> FetchPageAsync(HistoryRowColumn column, int shard, byte[] from, byte[] shardEnd, byte[]? sourceCursor, CancellationToken cancellationToken)
    {
        int timeouts = 0;
        int refusals = 0;
        while (true)
        {
            ArchiveCloneRowPage page;
            try
            {
                page = await _source.GetHistoryRowsAsync(column, from, shardEnd, sourceCursor, cancellationToken);
            }
            catch (TimeoutException) when (++timeouts < TimeoutRetryLimit)
            {
                if (_logger.IsInfo) _logger.Info($"Archive clone: {column} shard {shard} page timed out (attempt {timeouts}/{TimeoutRetryLimit}); the source may be unreachable, retrying in {RefusedRetryDelay.TotalSeconds:F0}s.");
                await Task.Delay(RefusedRetryDelay, cancellationToken);
                continue;
            }

            if (!page.Refused) return page;

            if (++refusals >= RefusedRetryLimit)
            {
                throw new InvalidOperationException($"The clone source kept refusing {column} rows for shard {shard} after {RefusedRetryLimit} attempts.");
            }

            if (refusals == 1 && _logger.IsInfo) _logger.Info($"Archive clone: the source is refusing {column} pages (it is likely persisting); waiting for it to resume.");
            else if (_logger.IsDebug) _logger.Debug($"Archive clone: {column} shard {shard} page refused by the source (attempt {refusals}/{RefusedRetryLimit}); retrying in {RefusedRetryDelay.TotalSeconds:F0}s.");
            await Task.Delay(RefusedRetryDelay, cancellationToken);
        }
    }

    /// <summary>Code rows land in the node's live code database, and code is read back by hash without ever being
    /// re-hashed, so an unchecked row would let a source put bytecode of its choosing behind a hash the EVM
    /// executes. The key is that hash, which makes the check exact and local: no peer, header or consensus data is
    /// needed to tell a forged row from a real one.</summary>
    private static bool IsCodeRowAuthentic(HistoryRowEntry entry)
        => entry.Key.Length == Hash256.Size && ValueKeccak.Compute(entry.Value.Span) == new ValueHash256(entry.Key);

    private static void WriteRow(IWriteBatch batch, HistoryRowEntry entry)
    {
        if (entry.Value.Length == 0)
        {
            batch.Set(entry.Key, Array.Empty<byte>());
            return;
        }

        batch.PutSpan(entry.Key, entry.Value.Span);
    }

    private void SyncDestination(HistoryRowColumn column, IDb destination)
    {
        if (column == HistoryRowColumn.Code) destination.SyncWal();
        else _history.SyncWal();
    }

    private IDb ResolveColumn(HistoryRowColumn column) => column switch
    {
        HistoryRowColumn.AccountHistory => _history.GetColumnDb(FlatHistoryColumns.AccountHistory),
        HistoryRowColumn.StorageHistory => _history.GetColumnDb(FlatHistoryColumns.StorageHistory),
        HistoryRowColumn.StorageClears => _history.GetColumnDb(FlatHistoryColumns.StorageClears),
        HistoryRowColumn.AvailableBlocks => _history.GetColumnDb(FlatHistoryColumns.AvailableBlocks),
        HistoryRowColumn.Code => _code,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
    };

    private static (byte[] Start, byte[] End) ShardBounds(int shard, int shardCount)
    {
        int startByte = shard * 256 / shardCount;
        int endByteInclusive = ((shard + 1) * 256 / shardCount) - 1;

        byte[] start = [(byte)startByte];
        byte[] end = new byte[IHistoryServer.MaxRowKeyBytes];
        end[0] = (byte)endByteInclusive;
        end.AsSpan(1).Fill(0xFF);
        return (start, end);
    }

    private byte[]? ReadShardCursor(HistoryRowColumn column, int shard) => _metadata.Get(CursorKey(column, shard));

    private void WriteShardCursor(HistoryRowColumn column, int shard, byte[] lastKey)
    {
        _metadata.PutSpan(CursorKey(column, shard), lastKey);
        _metadata.SyncWal();
    }

    private void ClearShardCursor(HistoryRowColumn column, int shard) => _metadata.Remove(CursorKey(column, shard));

    private static byte[] CursorKey(HistoryRowColumn column, int shard)
    {
        byte[] key = new byte[ShardCursorKeyPrefix.Length + 2];
        ShardCursorKeyPrefix.CopyTo(key);
        key[^2] = (byte)column;
        key[^1] = (byte)shard;
        return key;
    }
}
