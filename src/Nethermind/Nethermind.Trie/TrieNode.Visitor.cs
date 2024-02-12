// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
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
        /// <param name="visitor"></param>
        /// <param name="nodeResolver"></param>
        /// <param name="trieVisitContext"></param>
        /// <param name="nextToVisit"></param>
        /// <exception cref="InvalidDataException"></exception>
        /// <exception cref="TrieException"></exception>
        internal void AcceptResolvedNode<TNodeContext>(
            ITreeVisitor<TNodeContext> visitor,
            in TNodeContext nodeContext,
            ITrieNodeResolver nodeResolver,
            ref TreePath path,
            SmallTrieVisitContext trieVisitContext,
            IList<(TreePath, TrieNode, TNodeContext, SmallTrieVisitContext)> nextToVisit
        ) where TNodeContext : struct, INodeContext<TNodeContext>
        {
            switch (NodeType)
            {
                case NodeType.Branch:
                    {
                        visitor.VisitBranch(nodeContext, this, trieVisitContext.ToVisitContext());
                        trieVisitContext.Level++;

                        for (int i = 0; i < BranchesCount; i++)
                        {
                            TrieNode child = GetChild(nodeResolver, ref path, i);
                            if (child is not null)
                            {
                                int previousPathLength = AppendChildPath(ref path, i);
                                child.ResolveKey(nodeResolver, ref path, false);
                                TNodeContext childContext = nodeContext.Add((byte)i);
                                if (visitor.ShouldVisit(childContext, child.Keccak!))
                                {
                                    SmallTrieVisitContext childCtx = trieVisitContext; // Copy
                                    childCtx.BranchChildIndex = (byte?)i;

                                    nextToVisit.Add((path, child, childContext, childCtx));
                                }
                                path.TruncateMut(previousPathLength);

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

                            nextToVisit.Add((path, child, childContext, trieVisitContext));
                        }
                        path.TruncateMut(previousPathLength);

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
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;

                                if (TryResolveStorageRoot(nodeResolver, ref path, out TrieNode? chStorageRoot))
                                {
                                    Hash256 storageAddr;
                                    using (path.ScopedAppend(Key))
                                    {
                                        storageAddr = path.Path.ToCommitment();
                                    }
                                    trieVisitContext.Storage = storageAddr;
                                    TNodeContext storageContext = nodeContext.AddStorage(storageAddr);
                                    nextToVisit.Add((TreePath.Empty, chStorageRoot!, storageContext, trieVisitContext));
                                    trieVisitContext.Storage = null;
                                }
                                else
                                {
                                    visitor.VisitMissingNode(childContext, account.StorageRoot, trieVisitContext.ToVisitContext());
                                }
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
                            Parallel.For(0, BranchesCount, i =>
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
                            VisitSingleThread(ref path, visitor, nodeContext, nodeResolver, trieVisitContext);
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

                                    trieVisitContext.Storage = storageAccount;

                                    TNodeContext storageContext = leafContext.AddStorage(storageAccount);
                                    TreePath emptyPath = TreePath.Empty;
                                    storageRoot!.Accept(visitor, storageContext, nodeResolver.GetStorageTrieNodeResolver(storageAccount), ref emptyPath, trieVisitContext);

                                    trieVisitContext.Storage = null;
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
