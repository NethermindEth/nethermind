// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
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
/// This importer uses SetAccountRaw/SetStorageRaw with hash-based keys. For PreimageFlat mode,
/// wrap the persistence with PreimageRecordingPersistence and provide a previously recorded
/// preimage database - it will automatically translate raw operations to preimage-keyed operations.
/// </summary>
public class Importer(
    INodeStorage nodeStorage,
    IPersistence persistence,
    ILogManager logManager
)
{
    private readonly ILogger _logger = logManager.GetClassLogger<Importer>();
    private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;
    private long _totalNodes = 0;
    private const int BatchSize = 128_000;
    private const int FlushInterval = 50_000_000;
    private const int CheckCancelInterval = 100_000;

    private record struct Entry(Hash256? address, TreePath path, TrieNode node);

    public async Task Copy(StateId to, CancellationToken cancellationToken = default)
    {
        StateId from = new StateId();
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            from = reader.CurrentState;
        }

        ITrieStore trieStore = new RawTrieStore(nodeStorage);
        PatriciaTree tree = new PatriciaTree(trieStore, logManager);
        tree.RootHash = to.StateRoot.ToHash256();

        Channel<Entry> channel = Channel.CreateBounded<Entry>(2_000_000);
        if (_logger.IsWarn) _logger.Warn("Starting import");

        int maxConcurrency = 8;
        VisitorProgressTracker progressTracker = new VisitorProgressTracker("Flat Import", logManager);

        Task visitTask = Task.Run(() =>
        {
            Visitor visitor = new Visitor(channel.Writer, progressTracker, cancellationToken);
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
                channel.Writer.Complete();
            }
        }, cancellationToken);
        List<Task> tasks = new List<Task>();
        tasks.Add(visitTask);

        int concurrentIngestCount = Math.Min(Environment.ProcessorCount, maxConcurrency);

        tasks.AddRange(Enumerable.Range(0, concurrentIngestCount).Select((_) => Task.Run(async () =>
        {
            await IngestLogic(from, channel.Reader, cancellationToken);
        }, cancellationToken)));

        await Task.WhenAll(tasks);

        // Finally we increment the state id
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(from, to);
        writeBatch.Dispose();
        persistence.Flush();

        if (_logger.IsInfo) _logger.Info($"Flat db copy completed. Wrote {_totalNodes} nodes.");
    }

    private async Task IngestLogic(StateId from, ChannelReader<Entry> channelReader, CancellationToken cancellationToken = default)
    {
        if (_logger.IsInfo) _logger.Info($"Ingest thread started");

        int currentItemSize = 0;
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL); // It writes from initial state to initial state.
        await foreach (Entry entry in channelReader.ReadAllAsync(cancellationToken))
        {
            // Write it
            Metrics.ImporterEntriesCount++;

            TrieNode node = entry.node;
            if (entry.address is null)
            {
                writeBatch.SetStateTrieNode(entry.path, node);
            }
            else
            {
                writeBatch.SetStorageTrieNode(entry.address, entry.path, node);
            }
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = entry.path.Append(node.Key).Path;
                if (entry.address is null)
                {
                    Account acc = _accountDecoder.Decode(node.Value.Span)!;
                    writeBatch.SetAccountRaw(fullPath.ToHash256(), acc);
                }
                else
                {

                    ReadOnlySpan<byte> value = node.Value.Span;
                    byte[] toWrite;

                    if (value.IsEmpty)
                    {
                        toWrite = StorageTree.ZeroBytes;
                    }
                    else
                    {
                        Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
                        toWrite = rlp.DecodeByteArray();
                    }

                    writeBatch.SetStorageRaw(entry.address, fullPath.ToHash256(), SlotValue.FromSpanWithoutLeadingZero(toWrite));
                }
            }

            long theTotalNode = Interlocked.Increment(ref _totalNodes);
            if (theTotalNode % CheckCancelInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (theTotalNode % FlushInterval == 0)
            {
                writeBatch.Dispose();
                persistence.Flush();
                writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL); // It writes form initial state to initial state.
                currentItemSize = 0;
            }

            currentItemSize++;
            if (currentItemSize > BatchSize)
            {
                writeBatch.Dispose();
                writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL);
                currentItemSize = 0;
            }
        }

        writeBatch.Dispose();
    }

    private class Visitor(ChannelWriter<Entry> channelWriter, VisitorProgressTracker progressTracker, CancellationToken cancellationToken = default) : ITreeVisitor<TreePathContextWithStorage>
    {
        public bool IsFullDbScan => true;
        public bool ExpectAccounts => true;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash)
        {
            throw new Exception("Missing node is not expected");
        }

        private void Write(in TreePathContextWithStorage nodeContext, TrieNode node, bool isLeaf)
        {
            SpinWait sw = new SpinWait();
            while (!channelWriter.TryWrite(new Entry(nodeContext.Storage, nodeContext.Path, node)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sw.SpinOnce();
            }

            progressTracker.OnNodeVisited(nodeContext.Path, isStorage: nodeContext.Storage is not null, isLeaf);
        }

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            Write(nodeContext, node, isLeaf: false);
        }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            Write(nodeContext, node, isLeaf: false);
        }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            Write(nodeContext, node, isLeaf: true);
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account)
        {
        }

        public void Finish()
        {
            progressTracker.Finish();
        }
    }
}
