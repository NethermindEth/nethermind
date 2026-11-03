// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

/// <summary>
/// Imports state from trie-based persistence to flat persistence.
///
/// This importer uses SetAccountRaw/SetStorageRawEncoded with hash-based keys, so it cannot target a
/// preimage layout, which keys on the raw address and slot. <see cref="Nethermind.Db.FlatLayout.PreimageFlat"/>
/// and <see cref="Nethermind.Db.FlatLayout.PreimageFlatV1"/> are rejected before the import starts.
/// </summary>
public class Importer(
    INodeStorage nodeStorage,
    IPersistence persistence,
    ILogManager logManager,
    TimeProvider? timeProvider = null
)
{
    private readonly ILogger _logger = logManager.GetClassLogger<Importer>();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;
    private long _totalNodes = 0;
    private const int BatchSize = 128_000;
    private const int FlushInterval = 50_000_000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A write batch used by one traversal thread at a time, with the number of trie nodes written to it.
    /// </summary>
    private sealed class IngestBatch(IPersistence.IWriteBatch writeBatch)
    {
        public IPersistence.IWriteBatch WriteBatch { get; } = writeBatch;
        public int ItemCount { get; set; }
    }

    public async Task Copy(StateId to, CancellationToken cancellationToken = default)
    {
        StateId from;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            from = reader.CurrentState;
        }

        ITrieStore trieStore = new RawTrieStore(nodeStorage);
        PatriciaTree tree = new(trieStore, logManager)
        {
            RootHash = to.StateRoot.ToHash256()
        };

        ConcurrentBag<IngestBatch> ingestBatches = [];
        if (_logger.IsWarn) _logger.Warn($"Starting flat DB import. It can take hours; progress is logged every {ProgressInterval.TotalSeconds:0} s.");

        VisitorProgressTracker progressTracker = new("Flat Import", logManager);

        Task visitTask = Task.Run(() =>
        {
            Visitor visitor = new((address, path, node) => Ingest(from, ingestBatches, address, path, node), progressTracker, cancellationToken);
            try
            {
                tree.Accept(visitor, to.StateRoot.ToHash256(), new VisitingOptions()
                {
                    MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), // Tend to be faster with low thread
                });
            }
            finally
            {
                visitor.Finish();
                while (ingestBatches.TryTake(out IngestBatch? ingestBatch)) ingestBatch.WriteBatch.Dispose();
            }

            // A cancelled traversal skips the remaining nodes and returns normally.
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);

        // Timer-driven, so a line is written also while ingest is stalled and no node is visited.
        // After the traversal the estimate can stay below 100 % on small tries, so report it as done.
        ITimer progressTimer = _timeProvider.CreateTimer(
            _ => LogProgress(visitTask.IsCompletedSuccessfully && !cancellationToken.IsCancellationRequested ? 1 : progressTracker.GetProgress()),
            null, ProgressInterval, ProgressInterval);
        try
        {
            await visitTask;

            // The ingest batches use DisableWAL and are only crash-durable once flushed. Flush before advancing the
            // WAL-durable pointer, so a crash can't leave CurrentState at `to` over unflushed (holed) data, which
            // FlatDb.DropPruningTrieState would then treat as a finished import and delete the trie it came from.
            persistence.Flush();

            // An empty write batch from→to advances the persisted state ID to `to` without writing any data entries.
            IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(from, to);
            writeBatch.Dispose();
            persistence.Flush();
        }
        finally
        {
            // Waits for a running tick, so no progress line follows
            await progressTimer.DisposeAsync();
        }

        if (_logger.IsInfo) _logger.Info($"Flat db copy completed. Wrote {_totalNodes} nodes.");
    }

    private void LogProgress(double progress)
    {
        if (!_logger.IsInfo) return;

        _logger.Info($"Flat Import {progress.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter((float)progress, 1)} written: {Interlocked.Read(ref _totalNodes):N0}");
    }

    private void Ingest(in StateId from, ConcurrentBag<IngestBatch> ingestBatches, Hash256? address, in TreePath path, TrieNode node)
    {
        if (!ingestBatches.TryTake(out IngestBatch? ingestBatch))
        {
            ingestBatch = new IngestBatch(persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL)); // It writes from initial state to initial state.
        }

        IPersistence.IWriteBatch writeBatch = ingestBatch.WriteBatch;

        // Write it
        Metrics.ImporterEntriesCount++;

        if (address is null)
        {
            writeBatch.SetStateTrieNode(path, node.FullRlp.AsSpan());
        }
        else
        {
            writeBatch.SetStorageTrieNode(address, path, node.FullRlp.AsSpan());
        }

        if (node.IsLeaf)
        {
            ValueHash256 fullPath = path.Append(node.Key).Path;
            if (address is null)
            {
                RlpReader accountReader = new(node.Value.AsSpan());
                Account acc = _accountDecoder.Decode(ref accountReader)!;
                writeBatch.SetAccountRaw(fullPath, acc);
            }
            else
            {
                // A storage leaf value is RLP(stripped) and never empty (zero slots are absent), so it is
                // stored verbatim when wrapping, skipping a decode + re-encode round-trip.
                ReadOnlySpan<byte> value = node.Value.AsSpan();
                if (!value.IsEmpty)
                {
                    writeBatch.SetStorageRawEncoded(address, fullPath, value);
                }
            }
        }

        long theTotalNode = Interlocked.Increment(ref _totalNodes);
        if (theTotalNode % FlushInterval == 0)
        {
            writeBatch.Dispose();
            persistence.Flush();
        }
        else if (++ingestBatch.ItemCount > BatchSize)
        {
            writeBatch.Dispose();
        }
        else
        {
            ingestBatches.Add(ingestBatch);
        }
    }

    private class Visitor(Action<Hash256?, TreePath, TrieNode> ingest, VisitorProgressTracker progressTracker, CancellationToken cancellationToken = default) : ITreeVisitor<TreePathContextWithStorage>
    {
        public bool IsFullDbScan => true;
        public bool ExpectAccounts => true;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) =>
            !cancellationToken.IsCancellationRequested;

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new TrieException("Missing node is not expected");

        private void Write(in TreePathContextWithStorage nodeContext, TrieNode node, bool isLeaf)
        {
            ingest(nodeContext.Storage, nodeContext.Path, node);
            progressTracker.OnNodeVisited(nodeContext.Path, isStorage: nodeContext.Storage is not null, isLeaf);
        }

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) =>
            Write(nodeContext, node, isLeaf: false);

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) =>
            Write(nodeContext, node, isLeaf: false);

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) =>
            Write(nodeContext, node, isLeaf: true);

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }

        public void Finish() => progressTracker.Finish();
    }
}
