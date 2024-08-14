// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        private const int BranchesCount = 16;

        /// <summary>
        /// Like `Accept`, but does not execute its children. Instead it return the next trie to visit in the list
        /// `nextToVisit`. Also, it assume the node is already resolved.
        /// </summary>
        internal void AcceptResolvedNode<TNodeContext>(ITreeVisitor<TNodeContext> visitor, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, SmallTrieVisitContext trieVisitContext, IList<(TrieNode, TNodeContext, SmallTrieVisitContext)> nextToVisit)
            where TNodeContext : struct, INodeContext<TNodeContext>
        {
            // Note: The path is not maintained here, its just for a placeholder. This code is only used for BatchedTrieVisitor
            // which should only be used with hash keys.
            TreePath emptyPath = TreePath.Empty;
            switch (NodeType)
            {
                case NodeType.Branch:
                    {
                        visitor.VisitBranch(nodeContext, this, trieVisitContext.ToVisitContext());
                        trieVisitContext.Level++;

                        for (int i = 0; i < BranchesCount; i++)
                        {
                            TrieNode child = GetChild(nodeResolver, ref emptyPath, i);
                            if (child is not null)
                            {
                                child.ResolveKey(nodeResolver, ref emptyPath, false);
                                TNodeContext childContext = nodeContext.Add((byte)i);

                                if (visitor.ShouldVisit(childContext, child.Keccak!))
                                {
                                    SmallTrieVisitContext childCtx = trieVisitContext; // Copy
                                    childCtx.BranchChildIndex = (byte?)i;
                                    nextToVisit.Add((child, childContext, childCtx));
                                }

                                if (child.IsPersisted)
                                {
                                    UnresolveChild(i);
                                }
                            }
                        }

                        break;
                    }
                case NodeType.Extension:
                    {
                        visitor.VisitExtension(nodeContext, this, trieVisitContext.ToVisitContext());
                        TrieNode child = GetChild(nodeResolver, ref emptyPath, 0);
                        if (child is null)
                        {
                            throw new InvalidDataException($"Child of an extension {Key} should not be null.");
                        }

                        child.ResolveKey(nodeResolver, ref emptyPath, false);
                        TNodeContext childContext = nodeContext.Add(Key!);
                        if (visitor.ShouldVisit(childContext, child.Keccak!))
                        {
                            trieVisitContext.Level++;
                            trieVisitContext.BranchChildIndex = null;


                            nextToVisit.Add((child, childContext, trieVisitContext));
                        }

                        break;
                    }

                case NodeType.Leaf:
                    {
                        visitor.VisitLeaf(nodeContext, this, trieVisitContext.ToVisitContext(), Value.AsSpan());

                        if (!trieVisitContext.IsStorage && trieVisitContext.ExpectAccounts) // can combine these conditions
                        {
                            TNodeContext childContext = nodeContext.Add(Key!);

                            Account account = _accountDecoder.Decode(Value.AsRlpStream());
                            if (account.HasCode && visitor.ShouldVisit(childContext, account.CodeHash))
                            {
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;
                                visitor.VisitCode(childContext, account.CodeHash, trieVisitContext.ToVisitContext());
                                trieVisitContext.Level--;
                            }

                            if (account.HasStorage && visitor.ShouldVisit(childContext, account.StorageRoot))
                            {
                                trieVisitContext.IsStorage = true;
                                TNodeContext storageContext = childContext.AddStorage(account.StorageRoot);
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;

                                if (TryResolveStorageRoot(nodeResolver, ref emptyPath, out TrieNode? storageRoot))
                                {
                                    nextToVisit.Add((storageRoot!, storageContext, trieVisitContext));
                                }
                                else
                                {
                                    visitor.VisitMissingNode(storageContext, account.StorageRoot, trieVisitContext.ToVisitContext());
                                }

                                trieVisitContext.IsStorage = false;
                            }
                        }

                        break;
                    }

                default:
                    throw new TrieException($"An attempt was made to visit a node {Keccak} of type {NodeType}");
            }
        }

        internal void Accept(ITreeVisitor visitor, ITrieNodeResolver nodeResolver, ref TreePath path, TrieVisitContext trieVisitContext)
        {
            Accept(new ContextNotAwareTreeVisitor(visitor), default, nodeResolver, ref path, trieVisitContext);
        }

        internal void Accept<TNodeContext>(ITreeVisitor<TNodeContext> visitor, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, ref TreePath path, TrieVisitContext trieVisitContext)
            where TNodeContext : struct, INodeContext<TNodeContext>
        {
            try
            {
                ResolveNode(nodeResolver, path);
            }
            catch (TrieException)
            {
                visitor.VisitMissingNode(nodeContext, Keccak, trieVisitContext);
                return;
            }

            ResolveKey(nodeResolver, ref path, trieVisitContext.Level == 0);

            switch (NodeType)
            {
                case NodeType.Branch:
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitChild(ref TreePath path, int i, TrieNode? child, ITrieNodeResolver resolver, ITreeVisitor<TNodeContext> v, in TNodeContext nodeContext, TrieVisitContext context)
                        {
                            if (child is not null)
                            {
                                int previousPathLength = AppendChildPath(ref path, i);
                                child.ResolveKey(resolver, ref path, false);
                                TNodeContext childContext = nodeContext.Add((byte)i);
                                if (v.ShouldVisit(childContext, child.Keccak!))
                                {
                                    context.BranchChildIndex = i;
                                    child.Accept(v, childContext, resolver, ref path, context);
                                }
                                path.TruncateMut(previousPathLength);

                                if (child.IsPersisted)
                                {
                                    UnresolveChild(i);
                                }
                            }
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitSingleThread(ref TreePath parentPath, ITreeVisitor<TNodeContext> treeVisitor, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext)
                        {
                            // single threaded route
                            for (int i = 0; i < BranchesCount; i++)
                            {
                                VisitChild(ref parentPath, i, GetChild(trieNodeResolver, ref parentPath, i), trieNodeResolver, treeVisitor, nodeContext, visitContext);
                            }
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitMultiThread(TreePath parentPath, ITreeVisitor<TNodeContext> treeVisitor, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext, TrieNode?[] children)
                        {
                            var copy = nodeContext;

                            // multithreaded route
                            Parallel.For(0, BranchesCount, RuntimeInformation.ParallelOptionsPhysicalCores, i =>
                            {
                                visitContext.Semaphore.Wait();
                                try
                                {
                                    TreePath closureParentPath = parentPath;
                                    // we need to have separate context for each thread as context tracks level and branch child index
                                    TrieVisitContext childContext = visitContext.Clone();
                                    VisitChild(ref closureParentPath, i, children[i], trieNodeResolver, treeVisitor, copy, childContext);
                                }
                                finally
                                {
                                    visitContext.Semaphore.Release();
                                }
                            });
                        }

                        static void VisitAllSingleThread(TrieNode currentNode, ref TreePath path, ITreeVisitor<TNodeContext> visitor, TNodeContext nodeContext, ITrieNodeResolver nodeResolver, TrieVisitContext visitContext)
                        {
                            TrieNode?[] output = new TrieNode?[16];
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
                                    visitContext.BranchChildIndex = i;
                                    child.Accept(visitor, childContext, nodeResolver, ref path, visitContext);
                                }
                            }
                            path.TruncateOne();
                        }

                        visitor.VisitBranch(nodeContext, this, trieVisitContext);
                        trieVisitContext.AddVisited();
                        trieVisitContext.Level++;

                        if (trieVisitContext.MaxDegreeOfParallelism != 1 && trieVisitContext.Semaphore.CurrentCount > 1)
                        {
                            // we need to preallocate children
                            TrieNode?[] children = new TrieNode?[BranchesCount];
                            for (int i = 0; i < BranchesCount; i++)
                            {
                                children[i] = GetChild(nodeResolver, ref path, i);
                            }

                            if (trieVisitContext.Semaphore.CurrentCount > 1)
                            {
                                VisitMultiThread(path, visitor, nodeContext, nodeResolver, trieVisitContext, children);
                            }
                            else
                            {
                                VisitSingleThread(ref path, visitor, nodeContext, nodeResolver, trieVisitContext);
                            }
                        }
                        else
                        {
                            if (visitor.IsRangeScan)
                            {
                                VisitAllSingleThread(this, ref path, visitor, nodeContext, nodeResolver, trieVisitContext);
                            }
                            else
                            {
                                VisitSingleThread(ref path, visitor, nodeContext, nodeResolver, trieVisitContext);
                            }
                        }

                        trieVisitContext.Level--;
                        trieVisitContext.BranchChildIndex = null;
                        break;
                    }

                case NodeType.Extension:
                    {
                        visitor.VisitExtension(nodeContext, this, trieVisitContext);
                        trieVisitContext.AddVisited();
                        TrieNode child = GetChild(nodeResolver, ref path, 0);
                        if (child is null)
                        {
                            throw new InvalidDataException($"Child of an extension {Key} should not be null.");
                        }

                        int previousPathLength = AppendChildPath(ref path, 0);
                        child.ResolveKey(nodeResolver, ref path, false);
                        TNodeContext childContext = nodeContext.Add(Key!);
                        if (visitor.ShouldVisit(childContext, child.Keccak!))
                        {
                            trieVisitContext.Level++;
                            trieVisitContext.BranchChildIndex = null;
                            child.Accept(visitor, childContext, nodeResolver, ref path, trieVisitContext);
                            trieVisitContext.Level--;
                        }
                        path.TruncateMut(previousPathLength);

                        break;
                    }

                case NodeType.Leaf:
                    {
                        visitor.VisitLeaf(nodeContext, this, trieVisitContext, Value.AsSpan());

                        trieVisitContext.AddVisited();

                        TNodeContext leafContext = nodeContext.Add(Key!);

                        if (!trieVisitContext.IsStorage && trieVisitContext.ExpectAccounts) // can combine these conditions
                        {
                            Account account = _accountDecoder.Decode(Value.AsRlpStream());
                            if (account.HasCode && visitor.ShouldVisit(leafContext, account.CodeHash))
                            {
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;
                                visitor.VisitCode(leafContext, account.CodeHash, trieVisitContext);
                                trieVisitContext.Level--;
                            }

                            if (account.HasStorage && visitor.ShouldVisit(leafContext, account.StorageRoot))
                            {
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;

                                if (TryResolveStorageRoot(nodeResolver, ref path, out TrieNode? storageRoot))
                                {
                                    Hash256 storageAccount;
                                    using (path.ScopedAppend(Key))
                                    {
                                        storageAccount = path.Path.ToCommitment();
                                    }

                                    trieVisitContext.IsStorage = true;

                                    TNodeContext storageContext = leafContext.AddStorage(storageAccount);
                                    TreePath emptyPath = TreePath.Empty;
                                    storageRoot!.Accept(visitor, storageContext, nodeResolver.GetStorageTrieNodeResolver(storageAccount), ref emptyPath, trieVisitContext);

                                    trieVisitContext.IsStorage = false;
                                }
                                else
                                {
                                    visitor.VisitMissingNode(leafContext, account.StorageRoot, trieVisitContext);
                                }

                                trieVisitContext.Level--;
                            }
                        }

                        break;
                    }

                default:
                    throw new TrieException($"An attempt was made to visit a node {Keccak} of type {NodeType}");
            }
        }
    }
}
