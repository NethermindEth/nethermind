// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

/// <summary>
/// Rebuilds the Patricia trie NODES of a Flat-layout state DB from its already-present flat LEAVES.
///
/// The flat LEAVES (the Account column and Storage column raw key/value pairs) are the source of truth.
/// Trie internal nodes are 100% derivable from the leaves, so this recovery step inserts every leaf fresh
/// into an EMPTY trie (so no stale/corrupt node is ever read) and persists the freshly-computed nodes.
///
/// Only trie NODE columns are written (SetStateTrieNode / SetStorageTrieNode). The flat leaf columns
/// (Account / Storage raw leaves) are NEVER modified or deleted - they are the only recovery source and the
/// run must stay re-runnable.
///
/// Memory-bounded + parallel design:
/// - A SINGLE serialized writer thread (<see cref="NodeWriter"/>) owns the only write batch and performs all node
///   I/O. This is the provably-correct choice because the flat persistence write path is NOT provably thread-safe
///   for concurrent batch creation/dispose (the shared WriteBufferAdjuster on RocksDbPersistence and the metadata
///   SetCurrentState on batch dispose are mutated without synchronization).
/// - The huge ACCOUNT trie is rebuilt sequentially in fresh-tree-per-chunk passes; each chunk resolves only the
///   ~frontier from disk (read back from previously flushed nodes), so resident node count stays bounded per chunk.
/// - STORAGE (≈80% of the work, embarrassingly parallel over independent contracts) is rebuilt by a worker pool
///   that only does CPU-heavy hashing; committed nodes are funnelled to the single writer. Storage roots are
///   delivered back to the account loop IN SORTED ORDER via an in-order pipeline queue.
/// </summary>
public class FlatTrieRebuilder(IPersistence persistence, ILogManager logManager)
{
    private readonly IPersistence _persistence = persistence;
    private readonly ILogManager _logManager = logManager;
    private readonly ILogger _logger = logManager.GetClassLogger<FlatTrieRebuilder>();

    /// <summary>Accounts inserted per fresh-tree chunk of the (sequential) state trie pass.</summary>
    private const int AccountChunkSize = 200_000;

    /// <summary>Slots inserted per fresh-tree chunk of a storage trie. Small contracts finish in one chunk.</summary>
    private const int StorageChunkSize = 1_000_000;

    /// <summary>Trie nodes written by the single writer before it recycles its batch (bounds batch memory).</summary>
    private const int NodeBatchSize = 128_000;

    /// <summary>Trie nodes written by the single writer before a full persistence flush.</summary>
    private const int NodeFlushInterval = 8_000_000;

    /// <summary>Progress log cadence on the writer side.</summary>
    private const long NodeProgressInterval = 5_000_000;

    /// <summary>Bounded node channel capacity (back-pressures producers, caps resident node memory).</summary>
    private const int NodeChannelCapacity = 500_000;

    /// <summary>In-order storage-root pipeline depth (look-ahead over contracts).</summary>
    private const int StoragePipelineCapacity = 256;

    /// <summary>Cancellation check cadence while iterating leaves.</summary>
    private const int CheckCancelInterval = 100_000;

    private long _writtenNodes;

    /// <summary>A committed trie node destined for the flat node columns. Address == null means a state node.</summary>
    private readonly record struct NodeEntry(Hash256? Address, TreePath Path, TrieNode Node);

    /// <summary>
    /// Rebuilds all trie nodes from the flat leaves and returns the resulting (consistent-by-construction) state root.
    /// </summary>
    public async Task<Hash256> Rebuild(long targetBlockNumber, CancellationToken cancellationToken = default)
    {
        StateId from;
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            from = reader.CurrentState;
        }

        if (_logger.IsWarn) _logger.Warn($"Starting flat trie rebuild from leaves. Current state: {from}. Target block: {targetBlockNumber}.");

        int storageWorkerCount = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));
        Channel<NodeEntry> nodeChannel = Channel.CreateBounded<NodeEntry>(NodeChannelCapacity);
        NodeWriter nodeWriter = new(_persistence, from, nodeChannel, _logger, () => Interlocked.Read(ref _writtenNodes), () => Interlocked.Increment(ref _writtenNodes));

        // Dedicated long-running thread for the writer: producers' CommitNode spin synchronously on the bounded
        // node channel, so the single writer must always be schedulable independent of ThreadPool pressure.
        Task writerTask = Task.Factory.StartNew(
            () => nodeWriter.Run(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        Hash256 rebuiltRoot;
        try
        {
            rebuiltRoot = await BuildState(nodeWriter, nodeChannel.Writer, storageWorkerCount, cancellationToken);
        }
        catch (Exception)
        {
            nodeChannel.Writer.TryComplete();
            throw;
        }

        nodeChannel.Writer.Complete();
        await writerTask;

        _persistence.Flush();

        // Advance the persisted state id to the rebuilt root at the target block. This only writes the state-id
        // metadata; the leaf columns are never written by this code path.
        StateId to = new((ulong)targetBlockNumber, rebuiltRoot.ValueHash256);
        using (IPersistence.IWriteBatch stateIdBatch = _persistence.CreateWriteBatch(from, to))
        {
        }
        _persistence.Flush();

        if (_logger.IsWarn) _logger.Warn(
            $"Flat trie rebuild complete. Wrote {Interlocked.Read(ref _writtenNodes)} nodes. RECOVERED STATE ROOT: {rebuiltRoot} at block {targetBlockNumber}.");

        return rebuiltRoot;
    }

    /// <summary>
    /// Sequential state-trie pass (fresh tree per chunk) consuming storage roots from the in-order pipeline.
    /// Returns the recovered state root.
    /// </summary>
    private async Task<Hash256> BuildState(
        NodeWriter nodeWriter,
        ChannelWriter<NodeEntry> nodeChannel,
        int storageWorkerCount,
        CancellationToken cancellationToken)
    {
        using StorageRootPipeline pipeline = new(this, nodeWriter, nodeChannel, storageWorkerCount, cancellationToken);

        Hash256 runningRoot = Keccak.EmptyTreeHash;
        long accountCount = 0;
        long contractCount = 0;
        long chunkAccounts = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        StateTree? stateTree = null;
        IPersistence.IPersistenceReader? readbackReader = null;

        void StartChunk()
        {
            // Fresh reader so the trie resolves the just-flushed frontier of the previous chunk.
            readbackReader = _persistence.CreateReader();
            FlatReadbackTrieStore store = new(readbackReader, nodeChannel, address: null, cancellationToken);
            stateTree = new StateTree(store, _logManager) { RootHash = runningRoot };
        }

        async Task FinishChunkAsync()
        {
            stateTree!.Commit();
            runningRoot = stateTree.RootHash;
            readbackReader!.Dispose();
            stateTree = null;
            readbackReader = null;
            // Ensure all nodes for this chunk are flushed before the next chunk reads back its frontier.
            await nodeWriter.FlushBarrierAsync(cancellationToken);
        }

        StartChunk();

        await foreach (StorageRootResult result in pipeline.ConsumeInOrder(cancellationToken))
        {
            Account account = result.Account;
            if (result.HasStorage)
            {
                account = account.WithChangedStorageRoot(result.StorageRoot);
                contractCount++;
            }

            stateTree!.Set(result.HashedAddress, account);
            accountCount++;
            chunkAccounts++;

            if (accountCount % CheckCancelInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (chunkAccounts >= AccountChunkSize)
            {
                await FinishChunkAsync();
                chunkAccounts = 0;
                StartChunk();
                if (_logger.IsInfo) _logger.Info(
                    $"Rebuild progress: {accountCount} accounts ({contractCount} contracts) in {stopwatch.Elapsed}. Running root: {runningRoot.ToShortString()}. Prefix: {result.HashedAddress.Bytes[..4].ToHexString()}.");
            }
        }

        await FinishChunkAsync();

        if (_logger.IsInfo) _logger.Info(
            $"State trie built: {accountCount} accounts ({contractCount} contracts) in {stopwatch.Elapsed}. Root: {runningRoot}.");

        return runningRoot;
    }

    /// <summary>
    /// Rebuilds one contract's storage trie from its flat storage leaves (sorted) and returns its root.
    /// Chunked at <see cref="StorageChunkSize"/>: the common small contract finishes in a single in-memory chunk
    /// (no readback); a rare mega-contract continues from <c>runningRoot</c> after a writer flush barrier so each
    /// chunk only holds its own nodes plus the resolved frontier.
    /// </summary>
    private async Task<Hash256> RebuildStorage(
        NodeWriter nodeWriter,
        ChannelWriter<NodeEntry> nodeChannel,
        ValueHash256 hashedAddress,
        CancellationToken cancellationToken)
    {
        Hash256 addressHash = hashedAddress.ToHash256();
        Hash256 runningRoot = Keccak.EmptyTreeHash;
        long slotsInChunk = 0;
        bool multiChunk = false;

        IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        try
        {
            FlatReadbackTrieStore store = new(reader, nodeChannel, addressHash, cancellationToken);
            StorageTree storageTree = new(store, runningRoot, _logManager);

            using IPersistence.IFlatIterator storageIterator = reader.CreateStorageIterator(hashedAddress);
            while (storageIterator.MoveNext())
            {
                ValueHash256 slotKey = storageIterator.CurrentKey;

                // Flat storage leaves hold the raw 32-byte value (with leading zeros). Trim leading zeros and let
                // StorageTree RLP-encode it, matching how slots are written elsewhere (see StorageTree.Set / Importer).
                ReadOnlySpan<byte> rawValue = storageIterator.CurrentValue.WithoutLeadingZeros();
                byte[] value = rawValue.IsEmpty ? StorageTree.ZeroBytes : rawValue.ToArray();
                storageTree.Set(slotKey, value);

                slotsInChunk++;
                if (slotsInChunk >= StorageChunkSize)
                {
                    // Mega-contract: commit this chunk, flush via the single writer, then continue with a fresh tree
                    // and fresh reader so the frontier reads back the just-flushed nodes.
                    multiChunk = true;
                    storageTree.Commit();
                    runningRoot = storageTree.RootHash;
                    reader.Dispose();
                    await nodeWriter.FlushBarrierAsync(cancellationToken);

                    reader = _persistence.CreateReader();
                    store = new FlatReadbackTrieStore(reader, nodeChannel, addressHash, cancellationToken);
                    storageTree = new StorageTree(store, runningRoot, _logManager);
                    slotsInChunk = 0;
                }
            }

            storageTree.Commit();
            runningRoot = storageTree.RootHash;

            if (multiChunk)
            {
                // Make the final chunk's nodes durable before the account loop consumes this root for readback.
                await nodeWriter.FlushBarrierAsync(cancellationToken);
                if (_logger.IsInfo) _logger.Info($"Rebuilt mega-contract {addressHash.ToShortString()} storage root {runningRoot.ToShortString()}.");
            }

            return runningRoot;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// In-order pipeline that reads ahead over the sorted account iterator, dispatches storage rebuild jobs to a
    /// bounded worker pool, and yields results (account + storage root) back in the original sorted account order.
    /// </summary>
    private sealed class StorageRootPipeline : IDisposable
    {
        private readonly FlatTrieRebuilder _rebuilder;
        private readonly NodeWriter _nodeWriter;
        private readonly ChannelWriter<NodeEntry> _nodeChannel;
        private readonly SemaphoreSlim _workerSlots;
        private readonly Channel<Task<StorageRootResult>> _ordered;
        private readonly Task _producerTask;

        public StorageRootPipeline(
            FlatTrieRebuilder rebuilder,
            NodeWriter nodeWriter,
            ChannelWriter<NodeEntry> nodeChannel,
            int workerCount,
            CancellationToken cancellationToken)
        {
            _rebuilder = rebuilder;
            _nodeWriter = nodeWriter;
            _nodeChannel = nodeChannel;
            _workerSlots = new SemaphoreSlim(workerCount, workerCount);
            _ordered = Channel.CreateBounded<Task<StorageRootResult>>(StoragePipelineCapacity);
            _producerTask = Task.Run(() => ProduceAsync(cancellationToken), cancellationToken);
        }

        public async IAsyncEnumerable<StorageRootResult> ConsumeInOrder(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (Task<StorageRootResult> task in _ordered.Reader.ReadAllAsync(cancellationToken))
            {
                yield return await task;
            }
            await _producerTask;
        }

        private async Task ProduceAsync(CancellationToken cancellationToken)
        {
            try
            {
                using IPersistence.IPersistenceReader reader = _rebuilder._persistence.CreateReader();
                using IPersistence.IFlatIterator accountIterator = reader.CreateAccountIterator();

                AccountDecoder slimDecoder = AccountDecoder.Slim;

                while (accountIterator.MoveNext())
                {
                    ValueHash256 hashedAddress = accountIterator.CurrentKey;

                    RlpReader accountCtx = new(accountIterator.CurrentValue);
                    Account account = slimDecoder.Decode(ref accountCtx)
                        ?? throw new InvalidOperationException($"Failed to decode flat account leaf for {hashedAddress}.");

                    Task<StorageRootResult> resultTask;
                    if (account.HasStorage)
                    {
                        await _workerSlots.WaitAsync(cancellationToken);
                        ValueHash256 jobAddress = hashedAddress;
                        Account jobAccount = account;
                        resultTask = Task.Run(async () =>
                        {
                            try
                            {
                                Hash256 storageRoot = await _rebuilder.RebuildStorage(_nodeWriter, _nodeChannel, jobAddress, cancellationToken);
                                return new StorageRootResult(jobAddress, jobAccount, true, storageRoot);
                            }
                            finally
                            {
                                _workerSlots.Release();
                            }
                        }, cancellationToken);
                    }
                    else
                    {
                        resultTask = Task.FromResult(new StorageRootResult(hashedAddress, account, false, Keccak.EmptyTreeHash));
                    }

                    await _ordered.Writer.WriteAsync(resultTask, cancellationToken);
                }

                _ordered.Writer.Complete();
            }
            catch (Exception ex)
            {
                _ordered.Writer.TryComplete(ex);
                throw;
            }
        }

        public void Dispose() => _workerSlots.Dispose();
    }

    private readonly record struct StorageRootResult(
        ValueHash256 HashedAddress,
        Account Account,
        bool HasStorage,
        Hash256 StorageRoot);

    /// <summary>
    /// The single serialized node writer. It owns the only write batch and the only flush/batch-recreate, so all
    /// write-path access is single-threaded - provably correct given the persistence write path is not provably
    /// thread-safe for concurrent batch creation/dispose. Producers funnel committed nodes via the bounded channel
    /// and request durability via <see cref="FlushBarrierAsync"/> (ordered through the same channel).
    /// </summary>
    private sealed class NodeWriter(
        IPersistence persistence,
        StateId from,
        Channel<NodeEntry> channel,
        ILogger logger,
        Func<long> readWritten,
        Action incrementWritten)
    {
        // A sentinel NodeEntry whose Node is null cannot occur for a real commit (CommitNode never passes null).
        private static readonly NodeEntry BarrierEntry = new(null, default, null!);

        // Barrier completions, drained in the same FIFO order their sentinels are read from the node channel.
        private readonly Channel<TaskCompletionSource> _barriers = Channel.CreateUnbounded<TaskCompletionSource>();

        public async Task FlushBarrierAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Order matters: register the completion, then enqueue the sentinel so every node enqueued before this
            // barrier is drained (and written) before the writer flushes and signals.
            await _barriers.Writer.WriteAsync(tcs, cancellationToken);
            await channel.Writer.WriteAsync(BarrierEntry, cancellationToken);
            await tcs.Task;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            if (logger.IsInfo) logger.Info("Flat trie rebuild node-writer started.");

            long localWritten = 0;
            int batchItemCount = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL);
            try
            {
                await foreach (NodeEntry entry in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (entry.Node is null)
                    {
                        // Barrier: dispose+flush so prior writes are visible to subsequent readers, then signal.
                        writeBatch.Dispose();
                        persistence.Flush();
                        writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL);
                        batchItemCount = 0;

                        TaskCompletionSource tcs = await _barriers.Reader.ReadAsync(cancellationToken);
                        tcs.SetResult();
                        continue;
                    }

                    if (entry.Address is null)
                    {
                        writeBatch.SetStateTrieNode(entry.Path, entry.Node.FullRlp.AsSpan());
                    }
                    else
                    {
                        writeBatch.SetStorageTrieNode(entry.Address, entry.Path, entry.Node.FullRlp.AsSpan());
                    }

                    localWritten++;
                    incrementWritten();
                    batchItemCount++;

                    if (localWritten % CheckCancelInterval == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (localWritten % NodeProgressInterval == 0)
                    {
                        if (logger.IsInfo) logger.Info(
                            $"Rebuild wrote {readWritten()} trie nodes in {stopwatch.Elapsed}. Current path: {entry.Path}.");
                    }

                    if (localWritten % NodeFlushInterval == 0)
                    {
                        writeBatch.Dispose();
                        persistence.Flush();
                        writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL);
                        batchItemCount = 0;
                    }
                    else if (batchItemCount >= NodeBatchSize)
                    {
                        writeBatch.Dispose();
                        writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL);
                        batchItemCount = 0;
                    }
                }
            }
            finally
            {
                writeBatch.Dispose();
            }

            if (logger.IsInfo) logger.Info($"Flat trie rebuild node-writer finished. Wrote {readWritten()} nodes in {stopwatch.Elapsed}.");
        }
    }

    /// <summary>
    /// Write-only-via-channel trie store that READS BACK previously flushed nodes (path-keyed) so a fresh tree can
    /// resolve its frontier when continuing from a non-empty running root. Committed nodes are emitted to the single
    /// writer's channel rather than persisted directly. For the state tree address is null; for a storage tree it is
    /// the contract's hashed key.
    /// </summary>
    private sealed class FlatReadbackTrieStore(
        IPersistence.IPersistenceReader reader,
        ChannelWriter<NodeEntry> nodeChannel,
        Hash256? address,
        CancellationToken cancellationToken
    ) : AbstractMinimalTrieStore
    {
        private static readonly ConcurrencyController NoConcurrency = new(1);

        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            address is null
                ? reader.TryLoadStateRlp(path, flags)
                : reader.TryLoadStorageRlp(address, path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new Committer(nodeChannel, address, cancellationToken);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? storageAddress)
        {
            if (storageAddress is null) return this;
            return new FlatReadbackTrieStore(reader, nodeChannel, storageAddress, cancellationToken);
        }

        private sealed class Committer(
            ChannelWriter<NodeEntry> nodeChannel,
            Hash256? address,
            CancellationToken cancellationToken
        ) : AbstractMinimalCommitter(NoConcurrency)
        {
            public override TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                NodeEntry entry = new(address, path, node);
                SpinWait spinWait = new();
                while (!nodeChannel.TryWrite(entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    spinWait.SpinOnce();
                }

                return node;
            }
        }
    }
}
