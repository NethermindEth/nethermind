// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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
    int batchSize = 500_000;

    private record Entry(Hash256? address, TreePath path, TrieNode node);

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

        Channel<Entry> channel = Channel.CreateBounded<Entry>(1_000_000);

        Task visitTask = Task.Run(() =>
        {
            try
            {
                tree.Accept(new Visitor(channel.Writer), to.stateRoot.ToHash256(), new VisitingOptions()
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        List<Task> tasks = new List<Task>();
        tasks.Add(visitTask);

        int concurrentIngestCount = Environment.ProcessorCount;
        if (!persistence.SupportConcurrentWrites)
        {
            concurrentIngestCount = 0;
        }

        tasks.AddRange(Enumerable.Range(0, concurrentIngestCount).Select((_) => Task.Run(async () =>
        {
            await IngestLogic(from, channel.Reader);
        })));

        Task.WaitAll(tasks);

        // Finally we increment the state id
        var writeBatch = persistence.CreateWriteBatch(from, to);
        writeBatch.Dispose();

        _logger.Info($"Flat db copy completed. Wrote {totalNodes} nodes.");
    }

    private async Task IngestLogic(StateId from, ChannelReader<Entry> channelReader)
    {
            int currentBatchSize = 0;

            var writeBatch = persistence.CreateWriteBatch(from, from); // It writes form initial state to initial state.
            await foreach (var entry in channelReader.ReadAllAsync())
            {
                // Write it

                TrieNode node = entry.node;
                writeBatch.SetTrieNodes(entry.address, entry.path, node);
                if (node.IsLeaf)
                {
                    ValueHash256 fullPath = entry.path.Append(node.Key).Path;
                    if (entry.address is null)
                    {
                        Account acc = _accountDecoder.Decode(node.Value.Span);
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

                        writeBatch.SetStorageRaw(entry.address, fullPath.ToHash256(), toWrite);
                    }
                }

                currentBatchSize += 1;
                long theTotalNode = Interlocked.Increment(ref totalNodes);
                if (theTotalNode % batchSize == 0)
                {
                    _logger.Info($"Wrote {theTotalNode:N} nodes.");
                }

                if (currentBatchSize >= batchSize)
                {
                    writeBatch.Dispose();
                    writeBatch = persistence.CreateWriteBatch(from, from);
                    currentBatchSize = 0;
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
            while (!channelWriter.TryWrite(new Entry(nodeContext.Storage, nodeContext.Path, node)))
            {
                channelWriter.WaitToWriteAsync().AsTask().Wait();
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
