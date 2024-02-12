// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
            switch (NodeType)
            {
                case NodeType.Branch:
                    {
                        visitor.VisitBranch(nodeContext, this, trieVisitContext.ToVisitContext());
                        trieVisitContext.Level++;

                        for (int i = 0; i < BranchesCount; i++)
                        {
                            TrieNode child = GetChild(nodeResolver, i);
                            if (child is not null)
                            {
                                child.ResolveKey(nodeResolver, false);
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
                        TrieNode child = GetChild(nodeResolver, 0);
                        if (child is null)
                        {
                            throw new InvalidDataException($"Child of an extension {Key} should not be null.");
                        }

                        child.ResolveKey(nodeResolver, false);
                        TNodeContext childContext = nodeContext.Add(Key.ToNibble());
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
                            TNodeContext childContext = nodeContext.Add(Key!.ToNibble());

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
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;

                                if (TryResolveStorageRoot(nodeResolver, out TrieNode? storageRoot))
                                {
                                    nextToVisit.Add((storageRoot!, childContext, trieVisitContext));
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

        internal void Accept(ITreeVisitor visitor, ITrieNodeResolver nodeResolver, TrieVisitContext trieVisitContext)
        {
            Accept(new ContextNotAwareTreeVisitor(visitor), default, nodeResolver, trieVisitContext);
        }

        internal void Accept<TNodeContext>(ITreeVisitor<TNodeContext> visitor, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, TrieVisitContext trieVisitContext)
            where TNodeContext : struct, INodeContext<TNodeContext>
        {
            try
            {
                ResolveNode(nodeResolver);
            }
            catch (TrieException)
            {
                visitor.VisitMissingNode(nodeContext, Keccak, trieVisitContext);
                return;
            }

            ResolveKey(nodeResolver, trieVisitContext.Level == 0);

            switch (NodeType)
            {
                case NodeType.Branch:
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitChild(int i, TrieNode? child, ITrieNodeResolver resolver, ITreeVisitor<TNodeContext> v, in TNodeContext nodeContext, TrieVisitContext context)
                        {
                            if (child is not null)
                            {
                                child.ResolveKey(resolver, false);
                                TNodeContext childContext = nodeContext.Add((byte)i);

                                if (v.ShouldVisit(childContext, child.Keccak!))
                                {
                                    context.BranchChildIndex = i;
                                    child.Accept(v, childContext, resolver, context);
                                }

                                if (child.IsPersisted)
                                {
                                    UnresolveChild(i);
                                }
                            }
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitSingleThread(ITreeVisitor<TNodeContext> treeVisitor, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext)
                        {
                            // single threaded route
                            for (int i = 0; i < BranchesCount; i++)
                            {
                                VisitChild(i, GetChild(trieNodeResolver, i), trieNodeResolver, treeVisitor, nodeContext, visitContext);
                            }
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitMultiThread(ITreeVisitor<TNodeContext> treeVisitor, in TNodeContext nodeContext, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext, TrieNode?[] children)
                        {
                            var copy = nodeContext;

                            // multithreaded route
                            Parallel.For(0, BranchesCount, i =>
                            {
                                visitContext.Semaphore.Wait();
                                try
                                {
                                    // we need to have separate context for each thread as context tracks level and branch child index
                                    TrieVisitContext childContext = visitContext.Clone();

                                    VisitChild(i, children[i], trieNodeResolver, treeVisitor, copy, childContext);
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
                                children[i] = GetChild(nodeResolver, i);
                            }

                            if (trieVisitContext.Semaphore.CurrentCount > 1)
                            {
                                VisitMultiThread(visitor, nodeContext, nodeResolver, trieVisitContext, children);
                            }
                            else
                            {
                                VisitSingleThread(visitor, nodeContext, nodeResolver, trieVisitContext);
                            }
                        }
                        else
                        {
                            VisitSingleThread(visitor, nodeContext, nodeResolver, trieVisitContext);
                        }

                        trieVisitContext.Level--;
                        trieVisitContext.BranchChildIndex = null;
                        break;
                    }

                case NodeType.Extension:
                    {
                        visitor.VisitExtension(nodeContext, this, trieVisitContext);
                        trieVisitContext.AddVisited();
                        TrieNode child = GetChild(nodeResolver, 0);
                        if (child is null)
                        {
                            throw new InvalidDataException($"Child of an extension {Key} should not be null.");
                        }

                        child.ResolveKey(nodeResolver, false);
                        TNodeContext childContext = nodeContext.Add(Key!.ToNibble());
                        if (visitor.ShouldVisit(childContext, child.Keccak!))
                        {
                            trieVisitContext.Level++;
                            trieVisitContext.BranchChildIndex = null;
                            child.Accept(visitor, childContext, nodeResolver, trieVisitContext);
                            trieVisitContext.Level--;
                        }

                        break;
                    }

                case NodeType.Leaf:
                    {
                        visitor.VisitLeaf(nodeContext, this, trieVisitContext, Value.AsSpan());

                        trieVisitContext.AddVisited();

                        TNodeContext leafContext = nodeContext.Add(Key!.ToNibble());

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
                                trieVisitContext.IsStorage = true;
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;

                                if (TryResolveStorageRoot(nodeResolver, out TrieNode? storageRoot))
                                {
                                    storageRoot!.Accept(visitor, leafContext, nodeResolver, trieVisitContext);
                                }
                                else
                                {
                                    visitor.VisitMissingNode(leafContext, account.StorageRoot, trieVisitContext);
                                }

                                trieVisitContext.Level--;
                                trieVisitContext.IsStorage = false;
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
