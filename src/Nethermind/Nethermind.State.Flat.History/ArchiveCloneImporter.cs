// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.State.Flat.History;

public sealed class ArchiveCloneImporter
{
    private const int BlockBytes = sizeof(ulong);

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

    private readonly IArchiveCloneSource _source;
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IDb _code;
    private readonly IDb _metadata;
    private readonly HistoryWindowPruner _pruner;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly int _shardCount;
    private readonly int _shardBufferBudget;
    private readonly ILogger _logger;

    public ArchiveCloneImporter(
        IArchiveCloneSource source,
        IColumnsDb<FlatHistoryColumns> history,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IFlatDbConfig config,
        HistoryWindowPruner pruner,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
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
        _source = source;
        _history = history;
        _code = codeDb;
        _metadata = metadataDb;
        _pruner = pruner;
        _availability = availability;
        _rowFormat = rowFormat;
        _shardCount = Math.Max(1, config.HistoryImportShardCount);
        _shardBufferBudget = Math.Max(1, config.HistoryImportShardBufferBudgetEntries);
        _logger = logManager.GetClassLogger<ArchiveCloneImporter>();
    }

    public async Task CloneAsync(CancellationToken cancellationToken)
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

        foreach (HistoryRowColumn column in ColumnsInCloneOrder)
        {
            await CloneColumnAsync(column, cancellationToken);
        }

        _availability.PublishWatermark(_source.Watermark, _source.RowFormatVersion);
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
        IDb destination = ResolveColumn(column);
        (destination as ITunableDb)?.Tune(ITunableDb.TuneType.HeavyWrite);
        try
        {
            for (int shard = 0; shard < _shardCount; shard++)
            {
                (byte[] shardStart, byte[] shardEnd) = ShardBounds(shard, _shardCount);
                using (await _pruner.BeginBackfillAsync(cancellationToken))
                {
                    await CloneShardAsync(column, destination, shard, shardStart, shardEnd, cancellationToken);
                }
            }
        }
        finally
        {
            (destination as ITunableDb)?.Tune(ITunableDb.TuneType.Default);
        }
    }

    private async Task CloneShardAsync(HistoryRowColumn column, IDb destination, int shard, byte[] shardStart, byte[] shardEnd, CancellationToken cancellationToken)
    {
        byte[]? resumeCursor = ReadShardCursor(column, shard);
        byte[] from = resumeCursor is null ? shardStart : RawRowKeys.NextKeyAfter(resumeCursor);

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
                    ArchiveCloneRowPage page = await _source.GetHistoryRowsAsync(column, from, shardEnd, sourceCursor, cancellationToken);
                    if (page.Refused)
                    {
                        throw new InvalidOperationException($"The clone source refused {column} rows for shard {shard}.");
                    }

                    foreach (HistoryRowEntry entry in page.Entries)
                    {
                        if (column == HistoryRowColumn.AvailableBlocks && entry.Key.Length != BlockBytes) continue;

                        WriteRow(batch, entry);
                        lastWritten = entry.Key;
                        written++;
                    }

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
                from = RawRowKeys.NextKeyAfter(lastWritten);
            }

            if (exhausted) break;
        }

        ClearShardCursor(column, shard);
    }

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
