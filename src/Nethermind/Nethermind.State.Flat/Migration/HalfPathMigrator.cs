// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Migration;

/// <summary>Copies the HalfPath trie into flat in the background, advancing the <see cref="HalfPathMigrationCursor"/> chunk by chunk.</summary>
/// <remarks>
/// <para>
/// A chunk walks the trie of the persisted state in path order from the cursor, through a persistence reader that serves what is not
/// copied yet from the HalfPath DB, collecting about <see cref="IFlatDbConfig.MigrationChunkNodes"/> nodes outside any write batch. It writes them only if
/// the persisted state is still the one it walked, see <see cref="IPersistenceManager.TryWriteOnPersistedState"/>, so the copied values
/// are those readers of that state already see.
/// </para>
/// <para>
/// Chunks run on <see cref="IBackgroundTaskScheduler"/>, which cancels them when block processing starts, except while the node is
/// syncing, when they run alongside it. A cancelled chunk, a persist holding the lock or a persist since the walk drops the chunk,
/// which is walked again a little later.
/// </para>
/// </remarks>
public sealed class HalfPathMigrator(
    IPersistenceManager persistenceManager,
    HalfPathMigrationCursor cursor,
    IFlatDbConfig flatDbConfig,
    IBackgroundTaskScheduler scheduler,
    ITimerFactory timerFactory,
    IProcessExitSource processExitSource,
    ILogManager logManager) : IDisposable
{
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);
    private static readonly VisitingOptions InOrder = new() { MaxDegreeOfParallelism = 1 };

    private readonly ILogger _logger = logManager.GetClassLogger<HalfPathMigrator>();
    private readonly Core.Timers.ITimer _retryTimer = timerFactory.CreateTimer(RetryDelay);
    private long _nextProgressLog;

    private readonly record struct ChunkRequest : IBackgroundTaskRequest<ChunkRequest>;

    /// <summary>Starts copying from the cursor, if a migration is in progress.</summary>
    public void Start()
    {
        if (cursor.Current is null) return;
        if (_logger.IsInfo) _logger.Info("Copying the HalfPath trie state into the flat DB in the background.");
        _retryTimer.AutoReset = false;
        _retryTimer.Elapsed += (_, _) => Schedule();
        Schedule();
    }

    /// <inheritdoc/>
    public void Dispose() => _retryTimer.Dispose();

    private void Schedule()
    {
        if (processExitSource.Token.IsCancellationRequested) return;
        if (!scheduler.TryScheduleTask(default(ChunkRequest), RunChunk, ChunkTimeout)) ScheduleLater();
    }

    /// <summary>Schedules the next chunk after <see cref="RetryDelay"/>; chunks run one at a time, so one timer serves them all.</summary>
    private void ScheduleLater() => _retryTimer.Start();

    private Task RunChunk(ChunkRequest request, CancellationToken token)
    {
        if (cursor.Current is not { } from) return Task.CompletedTask;

        try
        {
            if (CopyChunk(from, token)) Schedule();
        }
        catch (Exception e)
        {
            // Resumes from the cursor on the next start.
            if (_logger.IsError) _logger.Error($"The HalfPath to flat migration stopped at {from}.", e);
        }

        return Task.CompletedTask;
    }

    /// <summary>Walks and writes one chunk from <paramref name="from"/>.</summary>
    /// <returns>Whether more is left to copy.</returns>
    private bool CopyChunk(in MigrationCursor from, CancellationToken token)
    {
        ChunkVisitor visitor = new(from, flatDbConfig.MigrationChunkNodes, token);
        StateId state;
        using (IPersistence.IPersistenceReader reader = persistenceManager.LeaseReader(ReaderFlags.FullScan))
        {
            state = reader.CurrentState;
            new PatriciaTree(new ReaderTrieStore(reader, null), logManager).Accept(visitor, state.StateRoot.ToCommitment(), InOrder);
        }

        MigrationCursor walkedFrom = from;
        bool written = false;
        if (token.IsCancellationRequested || !persistenceManager.TryWriteOnPersistedState(state, batch =>
            {
                // A wipe meanwhile abandons the migration.
                if (cursor.Current != walkedFrom) return;
                foreach ((Hash256? address, TreePath path, TrieNode node) in visitor.Nodes) Importer.WriteNode(batch, address, path, node);
                written = true;
            }))
        {
            ScheduleLater();
            return false;
        }

        if (!written) return false;
        Metrics.HalfPathMigratedNodes += visitor.Nodes.Count;

        if (visitor.IsComplete)
        {
            cursor.Complete();
            if (_logger.IsInfo) _logger.Info("The HalfPath to flat migration is complete.");
            return false;
        }

        cursor.Advance(visitor.Next);
        LogProgress(visitor.Next);
        return true;
    }

    private void LogProgress(in MigrationCursor copied)
    {
        long now = Environment.TickCount64;
        if (!_logger.IsInfo || now < _nextProgressLog || copied.Account is not { } account) return;
        _nextProgressLog = now + (long)ProgressInterval.TotalMilliseconds;
        double progress = BinaryPrimitives.ReadUInt64BigEndian(account.Bytes) / (double)ulong.MaxValue;
        _logger.Info($"HalfPath to flat migration {progress:P2} copied, at account {account}.");
    }

    /// <summary>Collects, in path order, the nodes after <paramref name="from"/> until <paramref name="limit"/> is reached at a leaf.</summary>
    private sealed class ChunkVisitor(MigrationCursor from, int limit, CancellationToken token) : ITreeVisitor<TreePathContextWithStorage>
    {
        private readonly MigrationCursor _from = from;
        private bool _full;

        public List<(Hash256? Address, TreePath Path, TrieNode Node)> Nodes { get; } = [];

        /// <summary>The cursor once <see cref="Nodes"/> are written.</summary>
        public MigrationCursor Next { get; private set; } = from;

        /// <summary>Whether the walk reached the end of the trie, so nothing is left once <see cref="Nodes"/> are written.</summary>
        public bool IsComplete => !_full && !token.IsCancellationRequested;

        public bool IsFullDbScan => false;

        // The walk follows key order, which the HalfPath and flat keys both mostly keep.
        public ReadFlags ExtraReadFlag => ReadFlags.HintReadAhead;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode)
        {
            if (_full || token.IsCancellationRequested) return false;
            if (_from.Account is not { } account) return true;

            // A subtree goes on once it holds a key at or past the cursor. A storage trie is asked for with its account
            // leaf's state context, whose full path then decides, so the storage of the cursor's account is entered.
            ValueHash256 upperBound = nodeContext.Path.ToUpperBoundPath();
            if (nodeContext.Storage is null) return upperBound.CompareTo(account) >= 0;

            int order = nodeContext.Storage.ValueHash256.CompareTo(account);
            return order > 0 || (order == 0 && (_from.Slot is not { } slot || upperBound.CompareTo(slot) > 0));
        }

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new TrieException($"Missing trie node {nodeHash} at {nodeContext.Path} of {nodeContext.Storage?.ToString() ?? "the state trie"}.");

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => Collect(nodeContext, node);

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => Collect(nodeContext, node);

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (!Collect(nodeContext, node)) return;

            ValueHash256 key = nodeContext.Path.Append(node.Key).Path;
            Next = nodeContext.Storage is null ? new MigrationCursor(key, null) : new MigrationCursor(nodeContext.Storage.ValueHash256, key);
            _full = Nodes.Count >= limit;
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }

        private bool Collect(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (_from.IsCopied(nodeContext.Storage, nodeContext.Path)) return false;
            Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
            return true;
        }
    }
}
