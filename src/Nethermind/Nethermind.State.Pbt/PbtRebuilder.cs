// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Rebuilds a canonical EIP-8297 tree in bounded staging windows.</summary>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, IPbtConfig config, int foldConcurrency, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();
    private readonly ConcurrencyController _foldQuota = new(foldConcurrency > 0 ? foldConcurrency : Environment.ProcessorCount);
    private readonly FoldFanOut _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);

    /// <summary>Folds leaf records into staged tree groups.</summary>
    /// <param name="source">Strictly ascending leaves.</param>
    /// <param name="windowSize">Maximum received records per update; zero uses 2,000,000 records.</param>
    /// <param name="cancellationToken">Cancels consumption and tree updates.</param>
    /// <returns>The completed canonical tree root.</returns>
    public ValueHash256 Rebuild(
        IEnumerator<RebuildEntry> source,
        int windowSize,
        CancellationToken cancellationToken)
    {
        if (windowSize == 0) windowSize = PbtSortedLeafFold.DefaultWindowSize;

        long receivedCount = 0;
        long committedWindows = 0;
        ValueHash256 root;
        using (StagingSink staging = new(target, cancellationToken))
        {
            root = PbtSortedLeafFold.FoldWindows(source, staging.Write, windowSize, _foldQuota, _foldFanOut, CommitWindow, cancellationToken);

            void CommitWindow(int leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                staging.Commit();
                receivedCount += leaves;
                committedWindows++;
            }
        }

        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete: {receivedCount} received leaves in {committedWindows} windows, tree root {root}");
        return root;
    }

    /// <summary>Writes a window's groups into one staging batch, committed once the window folds.</summary>
    /// <remarks>Not thread-safe: <see cref="PbtRightmostGroupStore"/> serializes its writes.</remarks>
    private sealed class StagingSink(PbtRocksDbPersistence target, CancellationToken cancellationToken) : IDisposable
    {
        private IPbtPersistence.IWriteBatch? _batch;

        public void Write(PbtGroupRecord record)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (_batch ??= target.CreateStagingWriteBatch()).SetNodeGroup(record.Path, record.Payload.Span);
        }

        public void Commit()
        {
            if (_batch is null) return;
            _batch.Commit();
            _batch.Dispose();
            _batch = null;
        }

        public void Dispose() => _batch?.Dispose();
    }
}
