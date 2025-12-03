// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
                    MaxDegreeOfParallelism = 1 // The writer is single threaded. Its better to write sorted then.
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        Task ingestTask = Task.Run(async () =>
        {
            long totalNodes = 0;
            int batchSize = 4_000_000;
            int currentBatchSize = 0;

            StateId lastState = from;
            var writeBatch = persistence.CreateWriteBatch(lastState, to);
            await foreach (var entry in channel.Reader.ReadAllAsync())
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
                totalNodes += 1;
                if (currentBatchSize >= batchSize)
                {
                    _logger.Info($"Wrote {totalNodes:N} nodes.");
                    writeBatch.Dispose();
                    lastState = to;
                    writeBatch = persistence.CreateWriteBatch(lastState, to);
                    currentBatchSize = 0;
                }
            }

            writeBatch.Dispose();
            _logger.Info($"Flat db copy completed. Wrote {totalNodes} nodes.");
        });

        Task.WaitAll(ingestTask, visitTask);
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
