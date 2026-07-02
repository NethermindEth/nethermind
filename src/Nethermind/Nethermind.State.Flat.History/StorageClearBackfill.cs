// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// One-time repair for history databases written before storage-clear capture existed: an account deletion always
/// implies its storage was cleared, and account history keys share the clear-marker layout
/// (<c>[account key | block BE]</c>), so every deletion tombstone's key is copied verbatim into the StorageClears
/// column. Idempotent (re-running writes the same keys), resumable (completion is marked in Metadata only after a
/// full scan), and safe to run while <see cref="HistoryWriter"/> captures new blocks. Until the scan completes,
/// slots shadowed by a pre-existing self-destruct may still read stale.
/// A destruct followed by a re-creation in the same block leaves no deletion tombstone, so that clear cannot be
/// synthesized from existing data; it is captured going forward by <see cref="HistoryWriter"/>.
/// </summary>
public sealed class StorageClearBackfill : IDisposable
{
    private const int AccountHistoryKeyLength = BaseFlatPersistence.AccountKeyLength + sizeof(ulong);
    private const int FlushBatchSize = 100_000;
    private const long ProgressLogInterval = 100_000_000;

    private static readonly byte[] CompletedKey = "storageClearBackfillCompleted"u8.ToArray();

    private static readonly TimeSpan NodeReadyPollInterval = TimeSpan.FromSeconds(5);

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly Func<bool> _isNodeProcessingBlocks;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _scan;

    public StorageClearBackfill(IColumnsDb<FlatHistoryColumns> history, Func<bool> isNodeProcessingBlocks, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(isNodeProcessingBlocks);
        _history = history;
        _isNodeProcessingBlocks = isNodeProcessingBlocks;
        _logger = logManager.GetClassLogger<StorageClearBackfill>();
    }

    public bool IsCompleted => _history.GetColumnDb(FlatHistoryColumns.Metadata).KeyExists(CompletedKey);

    public void Start()
    {
        if (IsCompleted) return;
        _scan = Task.Run(async () =>
        {
            try
            {
                // The scan is a full read of the account-history column; deferring it until the first block
                // capture keeps its IO out of DB opening and node initialization.
                while (!_isNodeProcessingBlocks())
                    await Task.Delay(NodeReadyPollInterval, _cancellation.Token);

                RunScan(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Storage-clear backfill failed; it will retry on next start.", e);
            }
        });
    }

    /// <summary>Runs the scan synchronously; exposed for tests and callable directly for offline repair.</summary>
    public void RunScan(CancellationToken cancellationToken)
    {
        if (IsCompleted) return;

        ISortedKeyValueStore accountHistory = (ISortedKeyValueStore)_history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        IDb storageClears = _history.GetColumnDb(FlatHistoryColumns.StorageClears);

        if (accountHistory.FirstKey is null)
        {
            MarkCompleted(scanned: 0, synthesized: 0);
            return;
        }

        byte[] lowerBound = new byte[AccountHistoryKeyLength];
        byte[] upperBound = new byte[AccountHistoryKeyLength + 1];
        upperBound.AsSpan().Fill(0xFF);

        long scanned = 0;
        long synthesized = 0;
        int pendingInBatch = 0;
        IWriteBatch batch = storageClears.StartWriteBatch();
        try
        {
            using ISortedView view = accountHistory.GetViewBetween(lowerBound, upperBound);
            while (view.MoveNext())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Storage-clear backfill interrupted after {scanned:N0} entries ({synthesized:N0} markers); it will resume on next start.");
                    return;
                }

                scanned++;
                if (scanned % ProgressLogInterval == 0 && _logger.IsInfo)
                    _logger.Info($"Storage-clear backfill progress: {scanned:N0} account-history entries scanned, {synthesized:N0} clear markers synthesized.");

                if (view.CurrentValue.Length != 0 || view.CurrentKey.Length != AccountHistoryKeyLength) continue;

                batch.Set(view.CurrentKey, Array.Empty<byte>());
                synthesized++;

                if (++pendingInBatch == FlushBatchSize)
                {
                    batch.Dispose();
                    batch = storageClears.StartWriteBatch();
                    pendingInBatch = 0;
                }
            }
        }
        finally
        {
            batch.Dispose();
        }

        MarkCompleted(scanned, synthesized);
    }

    private void MarkCompleted(long scanned, long synthesized)
    {
        _history.GetColumnDb(FlatHistoryColumns.Metadata).Set(CompletedKey, [1]);
        if (_logger.IsInfo) _logger.Info($"Storage-clear backfill completed: {scanned:N0} account-history entries scanned, {synthesized:N0} clear markers synthesized.");
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _scan?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException e) when (e.InnerException is OperationCanceledException)
        {
        }
        _cancellation.Dispose();
    }
}
