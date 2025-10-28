// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    partial class TrieNode
    {
        internal void Accept<TNodeContext>(ITreeVisitor<TNodeContext> visitor, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver,
            ref TreePath path, TrieVisitContext trieVisitContext) where TNodeContext : struct, INodeContext<TNodeContext>
        {
            new TrieNodeTraverser<TNodeContext>(visitor, trieVisitContext)
                .Start(this, nodeContext, nodeResolver, ref path);
        }
    }

    public class TrieNodeTraverser<TNodeContext>(ITreeVisitor<TNodeContext> visitor, TrieVisitContext options) where TNodeContext : struct, INodeContext<TNodeContext>
    {
        private static readonly AccountDecoder _accountDecoder = new();
        private int _maxDegreeOfParallelism = options.MaxDegreeOfParallelism;
        private ConcurrencyController _threadLimiter = new(options.MaxDegreeOfParallelism);
        private const long SubtreeSizeThreshold = 256 * 1024;

        private int _visitedNodes;

        internal void Start(TrieNode node, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, ref TreePath path)
        {
            _ = Accept(node, nodeContext, nodeResolver, ref path, options.IsStorage, long.MaxValue);
        }

        internal long Accept(TrieNode node, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, ref TreePath path, bool isStorage, long subtreeSizeHint)
        {
            try
            {
                node.ResolveNode(nodeResolver, path);
            }
            catch (TrieException)
            {
                visitor.VisitMissingNode(nodeContext, node.Keccak);
                return 1;
            }

            // Subtree size mechanism is used to prevent spawning of new task on storage that is estimated to be
            // too small. It works by having a subtree size hint obtained from previous child or parent or long.MaxValue
            // if not known yet, and using that estimate to not spawn task if below a certain threshold. This allow for
            // better use of concurrency slot.
            long actualSubtreeSize = 1; // one for itself
            subtreeSizeHint -= 1; // Consider self

            node.ResolveKey(nodeResolver, ref path);

            switch (node.NodeType)
            {
                case NodeType.Branch:
                    {
                        visitor.VisitBranch(nodeContext, node);
                        AddVisited();

                        // Limiting the multithread path to top state tree and first level storage double the throughput on mainnet.
                        // Top level state split to 16^3 while storage is 16, which should be ok for large contract in most case.
                        if (_maxDegreeOfParallelism != 1 && (isStorage ? path.Length <= 1 : path.Length <= 2))
                        {
                            actualSubtreeSize += VisitAllMultiThread(node, path, nodeContext, nodeResolver, isStorage, subtreeSizeHint);
                        }
                        else
                        {
                            if (visitor.IsRangeScan)
                            {
                                actualSubtreeSize += VisitAllSingleThread(node, ref path, nodeContext, nodeResolver, isStorage, subtreeSizeHint);
                            }
                            else
                            {
                                actualSubtreeSize += VisitSingleThread(node, ref path, nodeContext, nodeResolver, isStorage, subtreeSizeHint);
                            }
                        }

                        break;
                    }

                case NodeType.Extension:
                    {
                        visitor.VisitExtension(nodeContext, node);
                        AddVisited();
                        TrieNode child = node.GetChild(nodeResolver, ref path, 0) ?? throw new InvalidDataException($"Child of an extension {node.Key} should not be null.");
                        int previousPathLength = node.AppendChildPath(ref path, 0);
                        child.ResolveKey(nodeResolver, ref path);
                        TNodeContext childContext = nodeContext.Add(node.Key!);
                        if (visitor.ShouldVisit(childContext, child.Keccak!))
                        {
                            actualSubtreeSize += Accept(child, childContext, nodeResolver, ref path, isStorage, subtreeSizeHint);
                        }
                        path.TruncateMut(previousPathLength);

                        break;
                    }

                case NodeType.Leaf:
                    {
                        visitor.VisitLeaf(nodeContext, node);

                        AddVisited();

                        TNodeContext leafContext = nodeContext.Add(node.Key!);

                        if (!isStorage && visitor.ExpectAccounts)
                        {
                            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(node.Value.Span);
                            if (!_accountDecoder.TryDecodeStruct(ref decoderContext, out AccountStruct account))
                            {
                                throw new InvalidDataException("Non storage leaf should be an account");
                            }
                            visitor.VisitAccount(leafContext, node, account);

                            if (account.HasStorage && visitor.ShouldVisit(leafContext, account.StorageRoot))
                            {
                                if (node.TryResolveStorageRoot(nodeResolver, ref path, out TrieNode? storageRoot))
                                {
                                    Hash256 storageAccount;
                                    using (path.ScopedAppend(node.Key))
                                    {
                                        storageAccount = path.Path.ToCommitment();
                                    }

                                    TNodeContext storageContext = leafContext.AddStorage(storageAccount);
                                    TreePath emptyPath = TreePath.Empty;
                                    actualSubtreeSize += Accept(storageRoot!, storageContext, nodeResolver.GetStorageTrieNodeResolver(storageAccount), ref emptyPath, true, subtreeSizeHint);
                                }
                                else
                                {
                                    visitor.VisitMissingNode(leafContext, account.StorageRoot);
                                }
                            }
                        }

                        break;
                    }

                default:
                    throw new TrieException($"An attempt was made to visit a node {node.Keccak} of type {node.NodeType}");
            }

            return actualSubtreeSize;
        }

        private long VisitSingleThread(TrieNode node, ref TreePath path, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, bool isStorage, long subtreeSizeHint)
        {
            long actualSubtreeSize = 0;
            subtreeSizeHint /= TrieNode.BranchesCount; // Assume full branch
            // single threaded route
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? childNode = node.GetChild(trieNodeResolver, ref path, i);
                if (childNode is null) continue;
                int previousPathLength = node.AppendChildPath(ref path, i);
                childNode.ResolveKey(trieNodeResolver, ref path);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, childNode.Keccak!))
                {
                    // Note: Changing the subtreeSizeHint mid iteration is deliberate
                    subtreeSizeHint = Accept(childNode, childContext, trieNodeResolver, ref path, isStorage, subtreeSizeHint);
                    actualSubtreeSize += subtreeSizeHint;
                }

                if (childNode.IsPersisted)
                {
                    node.UnresolveChild(i);
                }

                path.TruncateMut(previousPathLength);
            }

            return actualSubtreeSize;
        }

        private long VisitAllSingleThread(TrieNode currentNode, ref TreePath path, TNodeContext nodeContext, ITrieNodeResolver nodeResolver, bool isStorage, long subtreeSizeHint)
        {
            RefList16<TrieNode?> output = new RefList16<TrieNode>(TrieNode.BranchesCount);
            int childCount = currentNode.ResolveAllChildBranch(nodeResolver, ref path, output.AsSpan());
            subtreeSizeHint /= childCount;
            long actualSubtreeSize = 0;

            path.AppendMut(0);
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                if (output[i] is null) continue;
                TrieNode child = output[i];
                path.SetLast(i);
                child.ResolveKey(nodeResolver, ref path);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, child.Keccak!))
                {
                    // Note: Changing the subtreeSizeHint mid iteration is deliberate
                    subtreeSizeHint = Accept(child, childContext, nodeResolver, ref path, isStorage, subtreeSizeHint);
                    actualSubtreeSize += subtreeSizeHint;
                }
            }
            path.TruncateOne();

            return actualSubtreeSize;
        }

        private long VisitAllMultiThread(TrieNode node, TreePath path, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, bool isStorage, long subtreeSizeHint)
        {
            RefList16<Task<long>> tasks = new RefList16<Task<long>>(0);

            // we need to preallocate children
            RefList16<TrieNode?> output = new RefList16<TrieNode?>(TrieNode.BranchesCount);
            int childCount = node.ResolveAllChildBranch(trieNodeResolver, ref path, output.AsSpan());

            subtreeSizeHint /= childCount;
            long actualSubtreeSize = 0;

            int handledChild = 0;

            path.AppendMut(0);
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                if (output[i] is null) continue;
                handledChild++;
                TrieNode child = output[i];
                path.SetLast(i);
                child.ResolveKey(trieNodeResolver, ref path);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, child.Keccak!))
                {
                    if (
                        handledChild < childCount // Not the last node
                        && subtreeSizeHint > SubtreeSizeThreshold
                        && _threadLimiter.TryTakeSlot(out ConcurrencyController.Slot returner))
                    {
                        tasks.Add(SpawnChildVisit(path, child, childContext, trieNodeResolver, returner, isStorage, subtreeSizeHint));
                    }
                    else
                    {
                        // Note: Changing the subtreeSizeHint mid iteration is deliberate
                        subtreeSizeHint = Accept(child, childContext, trieNodeResolver, ref path, isStorage, subtreeSizeHint);
                        actualSubtreeSize += subtreeSizeHint;
                    }
                }
            }
            path.TruncateOne();

            for (int i = 0; i < tasks.Count; i++)
            {
                actualSubtreeSize += tasks[i].Result;
            }

            return actualSubtreeSize;
        }

        private Task<long> SpawnChildVisit(TreePath path, TrieNode child, TNodeContext childContext, ITrieNodeResolver trieNodeResolver, ConcurrencyController.Slot slotReturner, bool isStorage, long subtreeSizeHint) =>
            Task.Run(() =>
            {
                using ConcurrencyController.Slot _ = slotReturner;

                return Accept(child, childContext, trieNodeResolver, ref path, isStorage, subtreeSizeHint);
            });

        private void AddVisited()
        {
            int visitedNodes = Interlocked.Increment(ref _visitedNodes);

            // TODO: Fine tune interval? Use TrieNode.GetMemorySize(false) to calculate memory usage?
            if (visitedNodes % 20_000_000 == 0)
            {
                GC.Collect();
            }
        }
    }
}
