// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;

namespace Nethermind.State.Flat.Importer;

public class Importer(
    INodeStorage nodeStorage,
    IPersistence persistence,
    ILogManager logManager
)
{
    ILogger _logger = logManager.GetClassLogger<Importer>();
    internal AccountDecoder _accountDecoder = AccountDecoder.Instance;
    long totalNodes = 0;
    int batchSize = 128_000;
    int flushInterval = 50_000_000;
    int logInterval = 1_000_000;

    private Histogram _importerTime = DevMetric.Factory.CreateHistogram("importer_time", "importer time", new HistogramConfiguration()
    {
        LabelNames = ["part"],
        Buckets = Histogram.PowersOfTenDividedBuckets(3, 9, 5)
    });

    private Counter _entriesCount = DevMetric.Factory.CreateCounter("importer_entries", "importer time");
    private Counter _entriesCountFlat = DevMetric.Factory.CreateCounter("importer_entries_flat", "importer time");

    private record struct Entry(Hash256? address, TreePath path, TrieNode node);

    public void Copy(StateId to)
    {
        StateId from = new StateId();
        using (var reader = persistence.CreateReader())
        {
            from = reader.CurrentState;
        }

        ITrieStore trieStore = new RawTrieStore(nodeStorage);
        PatriciaTree tree =  new PatriciaTree(trieStore, logManager);
        tree.RootHash = to.stateRoot.ToHash256();

        Channel<Entry> channel = Channel.CreateBounded<Entry>(2_000_000);
        _logger.Warn("Starting import");

        int maxConcurrency = 8;

        Task visitTask = Task.Run(() =>
        {
            try
            {
                tree.Accept(new Visitor(channel.Writer), to.stateRoot.ToHash256(), new VisitingOptions()
                {
                    MaxDegreeOfParallelism = 4,
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });
        List<Task> tasks = new List<Task>();
        tasks.Add(visitTask);

        if (persistence is IPersistenceWithConcurrentTrie concurrentTriePersistence)
        {
            _logger.Warn("Using concurrent trie");
            int concurrentIngestCount = Environment.ProcessorCount;
            if (!persistence.SupportConcurrentWrites)
            {
                concurrentIngestCount = 4;
            }
            concurrentIngestCount = Math.Min(concurrentIngestCount, maxConcurrency);

            Channel<Entry> flatChannel = Channel.CreateBounded<Entry>(2_000_000);

            Task[] trieTasks = (Enumerable.Range(0, concurrentIngestCount).Select((_) => Task.Run(async () =>
            {
                try
                {
                    await IngestLogicTrie(from, channel.Reader, flatChannel.Writer);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    throw;
                }
            })).ToArray());

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await IngestLogicFlatLMDB(from, flatChannel.Reader);
                    // await IngestLogicFlat(from, flatChannel.Reader);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    throw;
                }
            }));

            Task.WaitAll(trieTasks);
            flatChannel.Writer.Complete();

            Task.WaitAll(tasks);
        }
        else
        {
            int concurrentIngestCount = Environment.ProcessorCount;
            if (!persistence.SupportConcurrentWrites)
            {
                concurrentIngestCount = 1;
            }

            concurrentIngestCount = Math.Min(concurrentIngestCount, maxConcurrency);

            tasks.AddRange(Enumerable.Range(0, concurrentIngestCount).Select((_) => Task.Run(async () =>
            {
                await IngestLogic(from, channel.Reader);
            })));

            Task.WaitAll(tasks);
        }

        // Finally we increment the state id
        var writeBatch = persistence.CreateWriteBatch(from, to);
        writeBatch.Dispose();

        _logger.Info($"Flat db copy completed. Wrote {totalNodes} nodes.");
    }

    private async Task IngestLogic(StateId from, ChannelReader<Entry> channelReader)
    {
        _logger.Info($"Ingest thread started");

        int currentItemSize = 0;
        bool isFlush = false;
        var writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL); // It writes form initial state to initial state.
        await foreach (var entry in channelReader.ReadAllAsync())
        {
            // Write it
            _entriesCount.Inc();

            long sw = Stopwatch.GetTimestamp();
            TrieNode node = entry.node;
            writeBatch.SetTrieNodes(entry.address, entry.path, node);
            if (node.IsLeaf)
            {
                long isw = Stopwatch.GetTimestamp();
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
                _importerTime.WithLabels("leaf_set").Observe(Stopwatch.GetTimestamp() - isw);
            }

            long theTotalNode = Interlocked.Increment(ref totalNodes);
            if (theTotalNode % logInterval == 0)
            {
                _logger.Info($"Wrote {theTotalNode:N} nodes.");
            }

            if (theTotalNode % flushInterval == 0)
            {
                _logger.Info("Flushing next");
                sw = Stopwatch.GetTimestamp();
                writeBatch.Dispose();
                _importerTime.WithLabels("flush").Observe(Stopwatch.GetTimestamp() - sw);
                writeBatch = persistence.CreateWriteBatch(from, from); // It writes form initial state to initial state.
                currentItemSize = 0;
                isFlush = true;
            }

            _importerTime.WithLabels("flush_set").Observe(Stopwatch.GetTimestamp() - sw);

            currentItemSize++;
            if (currentItemSize > this.batchSize)
            {
                sw = Stopwatch.GetTimestamp();
                writeBatch.Dispose();
                if (isFlush)
                {
                    _importerTime.WithLabels("flush_really").Observe(Stopwatch.GetTimestamp() - sw);
                    Console.Error.WriteLine($"Hard flush too {Stopwatch.GetElapsedTime(sw)}");
                    isFlush = false;
                }
                else
                {
                    _importerTime.WithLabels("flush").Observe(Stopwatch.GetTimestamp() - sw);
                }
                writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL); // It writes form initial state to initial state.
                currentItemSize = 0;
            }
        }

        writeBatch.Dispose();
    }

    private async Task IngestLogicTrie(StateId from, ChannelReader<Entry> channelReader, ChannelWriter<Entry> flatWriter)
    {
        _logger.Info($"Ingest thread started trie");

        int currentItemSize = 0;
        bool isFlush = false;
        var writeBatch = ((IPersistenceWithConcurrentTrie) persistence).CreateTrieWriteBatch(WriteFlags.DisableWAL); // It writes form initial state to initial state.
        await foreach (var entry in channelReader.ReadAllAsync())
        {
            // Write it
            _entriesCount.Inc();

            long sw = Stopwatch.GetTimestamp();
            TrieNode node = entry.node;
            writeBatch.SetTrieNodes(entry.address, entry.path, node);
            _importerTime.WithLabels("flush_set").Observe(Stopwatch.GetTimestamp() - sw);

            if (node.IsLeaf)
            {
                await flatWriter.WriteAsync(entry);
            }

            long theTotalNode = Interlocked.Increment(ref totalNodes);
            if (theTotalNode % logInterval == 0)
            {
                _logger.Info($"Wrote {theTotalNode:N} nodes.");
            }

            if (theTotalNode % flushInterval == 0)
            {
                _logger.Info("Flushing next");
                sw = Stopwatch.GetTimestamp();
                writeBatch.Dispose();
                _importerTime.WithLabels("flush").Observe(Stopwatch.GetTimestamp() - sw);
                writeBatch = ((IPersistenceWithConcurrentTrie) persistence).CreateTrieWriteBatch(WriteFlags.DisableWAL);
                currentItemSize = 0;
                isFlush = true;
            }

            currentItemSize++;
            if (currentItemSize > this.batchSize)
            {
                sw = Stopwatch.GetTimestamp();
                writeBatch.Dispose();
                if (isFlush)
                {
                    _importerTime.WithLabels("flush_really").Observe(Stopwatch.GetTimestamp() - sw);
                    Console.Error.WriteLine($"Hard flush too {Stopwatch.GetElapsedTime(sw)}");
                    isFlush = false;
                }
                else
                {
                    _importerTime.WithLabels("flush").Observe(Stopwatch.GetTimestamp() - sw);
                }
                writeBatch = ((IPersistenceWithConcurrentTrie) persistence).CreateTrieWriteBatch(WriteFlags.DisableWAL);
                currentItemSize = 0;
            }
        }

        writeBatch.Dispose();
    }

    private async Task IngestLogicFlatLMDB(StateId from, ChannelReader<Entry> channelReader)
    {
        _logger.Info($"Ingest thread started flat");

        long totalFlat = 0;
        int currentItemSize = 0;
        using ArrayPoolList<Entry> entryBuffer = new ArrayPoolList<Entry>(0);

        void FlushBuffer(bool reallyFlush)
        {
            var writeBatch = persistence.CreateWriteBatch(from, from, reallyFlush ? WriteFlags.None : WriteFlags.DisableWAL); // It writes form initial state to initial state.

            foreach(var entry in entryBuffer)
            {
                TrieNode node = entry.node;
                long isw = Stopwatch.GetTimestamp();
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
                _importerTime.WithLabels("flat_set").Observe(Stopwatch.GetTimestamp() - isw);
            }
            entryBuffer.Clear();

            long sw = Stopwatch.GetTimestamp();
            Console.Error.WriteLine("Flush");
            writeBatch.Dispose();
            if (reallyFlush)
            {
                _importerTime.WithLabels("flush_really_flat").Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _importerTime.WithLabels("flush_flat").Observe(Stopwatch.GetTimestamp() - sw);
            }
        }

        await foreach (var entry in channelReader.ReadAllAsync())
        {
            // Write it
            _entriesCountFlat.Inc();

            entryBuffer.Add(entry);

            long theTotalNode = Interlocked.Increment(ref totalFlat);
            if (theTotalNode % flushInterval == 0)
            {
                _logger.Info("Flushing next");
                currentItemSize = 0;
                FlushBuffer(true);
            }

            currentItemSize++;
            if (currentItemSize > this.batchSize)
            {
                FlushBuffer(false);
                currentItemSize = 0;
            }
        }

        FlushBuffer(true);
    }

    private async Task IngestLogicFlat(StateId from, ChannelReader<Entry> channelReader)
    {
        _logger.Info($"Ingest thread started");

        long totalFlat = 0;
        int currentItemSize = 0;
        bool isFlush = false;
        var writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL); // It writes form initial state to initial state.
        await foreach (var entry in channelReader.ReadAllAsync())
        {
            // Write it
            _entriesCountFlat.Inc();

            long sw = Stopwatch.GetTimestamp();
            TrieNode node = entry.node;

            long isw = Stopwatch.GetTimestamp();
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
            _importerTime.WithLabels("flat_set").Observe(Stopwatch.GetTimestamp() - isw);

            long theTotalNode = Interlocked.Increment(ref totalFlat);
            if (theTotalNode % flushInterval == 0)
            {
                _logger.Info("Flushing next");
                sw = Stopwatch.GetTimestamp();
                writeBatch.Dispose();
                _importerTime.WithLabels("flush").Observe(Stopwatch.GetTimestamp() - sw);
                writeBatch = persistence.CreateWriteBatch(from, from); // It writes form initial state to initial state.
                currentItemSize = 0;
                isFlush = true;
            }

            currentItemSize++;
            if (currentItemSize > this.batchSize)
            {
                sw = Stopwatch.GetTimestamp();
                writeBatch.Dispose();
                if (isFlush)
                {
                    _importerTime.WithLabels("flush_really_flat").Observe(Stopwatch.GetTimestamp() - sw);
                    Console.Error.WriteLine($"Hard flush flat too {Stopwatch.GetElapsedTime(sw)}");
                    isFlush = false;
                }
                else
                {
                    _importerTime.WithLabels("flush_flat").Observe(Stopwatch.GetTimestamp() - sw);
                }
                writeBatch = persistence.CreateWriteBatch(from, from, WriteFlags.DisableWAL); // It writes form initial state to initial state.
                currentItemSize = 0;
            }
        }

        writeBatch.Dispose();
    }

    private class Visitor(ChannelWriter<Entry> channelWriter) : ITreeVisitor<TreePathContextWithStorage>
    {
        public bool IsFullDbScan => true;
        public bool ExpectAccounts => true;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode)
        {
            return true;
        }

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash)
        {
            throw new Exception("Missing node is not expected");
        }

        private void Write(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            SpinWait sw = new SpinWait();
            while (!channelWriter.TryWrite(new Entry(nodeContext.Storage, nodeContext.Path, node)))
            {
                sw.SpinOnce();
            }
        }

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            Write(nodeContext, node);
        }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            Write(nodeContext, node);
        }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            Write(nodeContext, node);
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account)
        {
        }
    }
}
