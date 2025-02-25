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
                .Accept(this, nodeContext, nodeResolver, ref path, trieVisitContext.IsStorage);
        }
    }

    public class TrieNodeTraverser<TNodeContext>(ITreeVisitor<TNodeContext> visitor, TrieVisitContext options) where TNodeContext : struct, INodeContext<TNodeContext>
    {
        private static readonly AccountDecoder _accountDecoder = new();
        private int _maxDegreeOfParallelism = options.MaxDegreeOfParallelism;
        private ConcurrencyController _threadLimiter = new(options.MaxDegreeOfParallelism);

        private int _visitedNodes;


        internal void Accept(TrieNode node, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, ref TreePath path, bool isStorage)
        {
            try
            {
                node.ResolveNode(nodeResolver, path);
            }
            catch (TrieException)
            {
                visitor.VisitMissingNode(nodeContext, node.Keccak);
                return;
            }

            node.ResolveKey(nodeResolver, ref path, path.Length == 0);

            switch (node.NodeType)
            {
                case NodeType.Branch:
                    {
                        visitor.VisitBranch(nodeContext, node);
                        AddVisited();

                        // Limiting the multithread path to top state tree and first level storage double the throughput on mainnet.
                        // Top level state split to 16^3 while storage is 16, which should be ok for large contract in most case.
                        if (_maxDegreeOfParallelism != 1 && (isStorage ? path.Length <= 0 : path.Length <= 2))
                        {
                            VisitAllMultiThread(node, path, nodeContext, nodeResolver, isStorage);
                        }
                        else
                        {
                            if (visitor.IsRangeScan)
                            {
                                VisitAllSingleThread(node, ref path, nodeContext, nodeResolver, isStorage);
                            }
                            else
                            {
                                VisitSingleThread(node, ref path, nodeContext, nodeResolver, isStorage);
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
                        child.ResolveKey(nodeResolver, ref path, false);
                        TNodeContext childContext = nodeContext.Add(node.Key!);
                        if (visitor.ShouldVisit(childContext, child.Keccak!))
                        {
                            Accept(child, childContext, nodeResolver, ref path, isStorage);
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
                            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(node.Value.AsSpan());
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
                                    Accept(storageRoot!, storageContext, nodeResolver.GetStorageTrieNodeResolver(storageAccount), ref emptyPath, true);
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

        }

        private void VisitSingleThread(TrieNode node, ref TreePath path, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, bool isStorage)
        {
            // single threaded route
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? childNode = node.GetChild(trieNodeResolver, ref path, i);
                if (childNode is null) continue;
                int previousPathLength = node.AppendChildPath(ref path, i);
                childNode.ResolveKey(trieNodeResolver, ref path, false);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, childNode.Keccak!))
                {
                    Accept(childNode, childContext, trieNodeResolver, ref path, isStorage);
                }

                if (childNode.IsPersisted)
                {
                    node.UnresolveChild(i);
                }

                path.TruncateMut(previousPathLength);
            }
        }

        private void VisitAllSingleThread(TrieNode currentNode, ref TreePath path, TNodeContext nodeContext, ITrieNodeResolver nodeResolver, bool isStorage)
        {
            using ArrayPoolList<TrieNode?> output = new ArrayPoolList<TrieNode>(TrieNode.BranchesCount, TrieNode.BranchesCount);
            currentNode.ResolveAllChildBranch(nodeResolver, ref path, output.AsSpan());

            path.AppendMut(0);
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                if (output[i] is null) continue;
                TrieNode child = output[i];
                path.SetLast(i);
                child.ResolveKey(nodeResolver, ref path, false);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, child.Keccak!))
                {
                    Accept(child, childContext, nodeResolver, ref path, isStorage);
                }
            }
            path.TruncateOne();
        }

        private void VisitAllMultiThread(TrieNode node, TreePath path, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, bool isStorage)
        {
            ArrayPoolList<Task>? tasks = null;

            // we need to preallocate children
            using ArrayPoolList<TrieNode?> output = new ArrayPoolList<TrieNode>(TrieNode.BranchesCount, TrieNode.BranchesCount);
            int childCount = node.ResolveAllChildBranch(trieNodeResolver, ref path, output.AsSpan());
            int handledChild = 0;

            path.AppendMut(0);
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                if (output[i] is null) continue;
                handledChild++;
                TrieNode child = output[i];
                path.SetLast(i);
                child.ResolveKey(trieNodeResolver, ref path, false);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, child.Keccak!))
                {
                    if (
                        handledChild < childCount // Not the last node
                        && _threadLimiter.TryTakeSlot(out ConcurrencyController.Slot returner))
                    {
                        tasks ??= new ArrayPoolList<Task>(TrieNode.BranchesCount);

                        tasks.Add(SpawnChildVisit(path, child, childContext, trieNodeResolver, returner, isStorage));
                    }
                    else
                    {
                        Accept(child, childContext, trieNodeResolver, ref path, isStorage);
                    }
                }
            }
            path.TruncateOne();

            if (tasks is { Count: > 0 })
            {
                Task.WaitAll((ReadOnlySpan<Task>)tasks.AsSpan());
                tasks.Dispose();
            }
        }

        private Task SpawnChildVisit(TreePath path, TrieNode child, TNodeContext childContext, ITrieNodeResolver trieNodeResolver, ConcurrencyController.Slot slotReturner, bool isStorage) =>
            Task.Run(() =>
            {
                using ConcurrencyController.Slot _ = slotReturner;

                Accept(child, childContext, trieNodeResolver, ref path, isStorage);
            });

        private void AddVisited()
        {
            int visitedNodes = Interlocked.Increment(ref _visitedNodes);

            // TODO: Fine tune interval? Use TrieNode.GetMemorySize(false) to calculate memory usage?
            if (visitedNodes % 10_000_000 == 0)
            {
                GC.Collect();
            }
        }
    }
}
