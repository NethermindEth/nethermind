// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
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
        internal long Accept<TNodeContext>(ITreeVisitor<TNodeContext> visitor, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver,
            ref TreePath path, TrieVisitContext trieVisitContext, long subtreeCountHintT) where TNodeContext : struct, INodeContext<TNodeContext>
        {
            return new TrieNodeTraverser<TNodeContext>(visitor).Accept(this, nodeContext, nodeResolver, ref path, trieVisitContext, subtreeCountHintT);
        }
    }

    public class TrieNodeTraverser<TNodeContext>(ITreeVisitor<TNodeContext> visitor) where TNodeContext : struct, INodeContext<TNodeContext>
    {
        private static readonly AccountDecoder _accountDecoder = new();

        private const long MinSubtreeSizeToParallelize = 1024 * 1024;
        internal long Accept(TrieNode node, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, ref TreePath path, TrieVisitContext trieVisitContext, long subtreeCountHintT)
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

            long actualSubtreeSize = 1;
            if (subtreeCountHintT > 0)
            {
                subtreeCountHintT--;
            }

            node.ResolveKey(nodeResolver, ref path, trieVisitContext.Level == 0);

            switch (node.NodeType)
            {
                case NodeType.Branch:
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        long VisitChild(ref TreePath path, int i, TrieNode child, ITrieNodeResolver resolver, ITreeVisitor<TNodeContext> v, in TNodeContext nodeContext, TrieVisitContext context, long subtreeCountHint)
                        {
                            long actualSubtreeSize = 0;
                            int previousPathLength = node.AppendChildPath(ref path, i);
                            child.ResolveKey(resolver, ref path, false);
                            TNodeContext childContext = nodeContext.Add((byte)i);
                            if (v.ShouldVisit(childContext, child.Keccak!))
                            {
                                actualSubtreeSize += Accept(child, childContext, resolver, ref path, context, subtreeCountHint);
                            }
                            path.TruncateMut(previousPathLength);

                            if (child.IsPersisted)
                            {
                                node.UnresolveChild(i);
                            }

                            return actualSubtreeSize;
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        long VisitSingleThread(ref TreePath parentPath, ITreeVisitor<TNodeContext> treeVisitor, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext, long subtreeCountHint)
                        {
                            long actualSubtreeSize = 0;
                            // single threaded route
                            for (int i = 0; i < TrieNode.BranchesCount; i++)
                            {
                                TrieNode? childNode = node.GetChild(trieNodeResolver, ref parentPath, i);
                                if (childNode is null) continue;
                                actualSubtreeSize += VisitChild(ref parentPath, i, childNode, trieNodeResolver, treeVisitor, nodeContext, visitContext, subtreeCountHint);
                            }

                            return actualSubtreeSize;
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        long VisitMultiThread(TreePath parentPath, ITreeVisitor<TNodeContext> treeVisitor, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext, long subtreeCountHint)
                        {
                            // we need to preallocate children
                            TNodeContext contextCopy = nodeContext;

                            long actualSubtreeSize = 0;
                            ArrayPoolList<Task<long>>? tasks = null;
                            for (int i = 0; i < TrieNode.BranchesCount; i++)
                            {
                                TrieNode? childNode = node.GetChild(trieNodeResolver, ref parentPath, i);
                                if (childNode is null) continue;

                                if (i < TrieNode.BranchesCount - 1 && subtreeCountHint > MinSubtreeSizeToParallelize && visitContext.ConcurrencyController.TryTakeSlot(out ConcurrencyController.Slot returner))
                                {
                                    tasks ??= new ArrayPoolList<Task<long>>(TrieNode.BranchesCount);

                                    // we need to have separate context for each thread as context tracks level and branch child index
                                    TrieVisitContext childContext = visitContext.Clone();
                                    tasks.Add(SpawnChildVisit(parentPath, i, childNode, returner, childContext, subtreeCountHint));
                                }
                                else
                                {
                                    long childSubtreeSize = VisitChild(ref parentPath, i, childNode, trieNodeResolver, treeVisitor, contextCopy, visitContext, subtreeCountHint);
                                    subtreeCountHint = childSubtreeSize;
                                    actualSubtreeSize += childSubtreeSize;
                                }
                            }

                            if (tasks is { Count: > 0 })
                            {
                                foreach (var childSubtreeSize in Task.WhenAll((ReadOnlySpan<Task<long>>)tasks.AsSpan()).Result)
                                {
                                    actualSubtreeSize += childSubtreeSize;
                                }
                                tasks.Dispose();
                            }

                            return actualSubtreeSize;

                            Task<long> SpawnChildVisit(TreePath closureParentPath, int i, TrieNode childNode, ConcurrencyController.Slot slotReturner, TrieVisitContext childContext, long subtreeCountHint) =>
                                Task.Run(() =>
                                {
                                    using ConcurrencyController.Slot _ = slotReturner;

                                    return VisitChild(ref closureParentPath, i, childNode, trieNodeResolver, treeVisitor, contextCopy, childContext, subtreeCountHint);
                                });
                        }

                        visitor.VisitBranch(nodeContext, node);
                        trieVisitContext.AddVisited();
                        trieVisitContext.Level++;

                        long childSubTreeHint = subtreeCountHintT / 16; // Assume full branch

                        // Limiting the multithread path to top state tree and first level storage double the throughput on mainnet.
                        // Top level state split to 16^3 while storage is 16, which should be ok for large contract in most case.
                        if (trieVisitContext.MaxDegreeOfParallelism != 1 && subtreeCountHintT > MinSubtreeSizeToParallelize && (trieVisitContext.IsStorage ? path.Length <= 1 : path.Length <= 2))
                        {
                            actualSubtreeSize += VisitMultiThread(path, visitor, nodeContext, nodeResolver, trieVisitContext, childSubTreeHint);
                        }
                        else
                        {
                            if (visitor.IsRangeScan)
                            {
                                actualSubtreeSize += VisitAllSingleThread(node, ref path, visitor, nodeContext, nodeResolver, trieVisitContext, childSubTreeHint);
                            }
                            else
                            {
                                actualSubtreeSize += VisitSingleThread(ref path, visitor, nodeContext, nodeResolver, trieVisitContext, childSubTreeHint);
                            }
                        }

                        trieVisitContext.Level--;
                        break;
                    }

                case NodeType.Extension:
                    {
                        visitor.VisitExtension(nodeContext, node);
                        trieVisitContext.AddVisited();
                        TrieNode child = node.GetChild(nodeResolver, ref path, 0) ?? throw new InvalidDataException($"Child of an extension {node.Key} should not be null.");
                        int previousPathLength = node.AppendChildPath(ref path, 0);
                        child.ResolveKey(nodeResolver, ref path, false);
                        TNodeContext childContext = nodeContext.Add(node.Key!);
                        if (visitor.ShouldVisit(childContext, child.Keccak!))
                        {
                            trieVisitContext.Level++;
                            actualSubtreeSize += Accept(child, childContext, nodeResolver, ref path, trieVisitContext, subtreeCountHintT);
                            trieVisitContext.Level--;
                        }
                        path.TruncateMut(previousPathLength);

                        break;
                    }

                case NodeType.Leaf:
                    {
                        visitor.VisitLeaf(nodeContext, node);

                        trieVisitContext.AddVisited();

                        TNodeContext leafContext = nodeContext.Add(node.Key!);

                        if (!trieVisitContext.IsStorage && visitor.ExpectAccounts) // can combine these conditions
                        {
                            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(node.Value.AsSpan());
                            if (!_accountDecoder.TryDecodeStruct(ref decoderContext, out AccountStruct account))
                            {
                                throw new InvalidDataException("Non storage leaf should be an account");
                            }
                            visitor.VisitAccount(leafContext, node, account);

                            if (account.HasStorage && visitor.ShouldVisit(leafContext, account.StorageRoot))
                            {
                                trieVisitContext.Level++;

                                if (node.TryResolveStorageRoot(nodeResolver, ref path, out TrieNode? storageRoot))
                                {
                                    Hash256 storageAccount;
                                    using (path.ScopedAppend(node.Key))
                                    {
                                        storageAccount = path.Path.ToCommitment();
                                    }

                                    trieVisitContext.IsStorage = true;

                                    TNodeContext storageContext = leafContext.AddStorage(storageAccount);
                                    TreePath emptyPath = TreePath.Empty;
                                    actualSubtreeSize += Accept(storageRoot!, storageContext, nodeResolver.GetStorageTrieNodeResolver(storageAccount), ref emptyPath, trieVisitContext, subtreeCountHintT);

                                    trieVisitContext.IsStorage = false;
                                }
                                else
                                {
                                    visitor.VisitMissingNode(leafContext, account.StorageRoot);
                                }

                                trieVisitContext.Level--;
                            }
                        }

                        break;
                    }

                default:
                    throw new TrieException($"An attempt was made to visit a node {node.Keccak} of type {node.NodeType}");
            }

            return actualSubtreeSize;
        }

        private long VisitAllSingleThread(TrieNode currentNode, ref TreePath path, ITreeVisitor<TNodeContext> visitor, TNodeContext nodeContext, ITrieNodeResolver nodeResolver, TrieVisitContext visitContext, long subtreeCountHint)
        {
            long actualSubtreeSize = 0;
            TrieNode?[] output = new TrieNode?[TrieNode.BranchesCount];
            currentNode.ResolveAllChildBranch(nodeResolver, ref path, output);
            path.AppendMut(0);
            for (int i = 0; i < 16; i++)
            {
                if (output[i] is null) continue;
                TrieNode child = output[i];
                path.SetLast(i);
                child.ResolveKey(nodeResolver, ref path, false);
                TNodeContext childContext = nodeContext.Add((byte)i);
                if (visitor.ShouldVisit(childContext, child.Keccak!))
                {
                    actualSubtreeSize += Accept(child, childContext, nodeResolver, ref path, visitContext, subtreeCountHint);
                }
            }
            path.TruncateOne();

            return actualSubtreeSize;
        }

    }
}
