// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        public const int BranchesCount = 16;

        internal void Accept(ITreeVisitor visitor, ITrieNodeResolver nodeResolver, TrieVisitContext trieVisitContext)
        {
            try
            {
                ResolveNode(nodeResolver);
            }
            catch (TrieException)
            {
                visitor.VisitMissingNode(Keccak, trieVisitContext);
                return;
            }

            ResolveKey(nodeResolver, trieVisitContext.Level == 0);

            switch (NodeType)
            {
                case NodeType.Branch:
                    {
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitChild(int i, TrieNode? child, ITrieNodeResolver resolver, ITreeVisitor v, TrieVisitContext context)
                        {
                            if (child is not null)
                            {
                                child.ResolveKey(resolver, false);
                                if (v.ShouldVisit(child.Keccak!))
                                {
                                    context.BranchChildIndex = i;
                                    child.Accept(v, resolver, context);
                                }

                                if (child.IsPersisted)
                                {
                                    UnresolveChild(i);
                                }
                            }
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitSingleThread(ITreeVisitor treeVisitor, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext)
                        {
                            // single threaded route
                            for (int i = 0; i < BranchesCount; i++)
                            {
                                using (trieVisitContext.AbsolutePathNext((byte)i))
                                {
                                    TrieNode? child = nodeResolver.Capability switch
                                    {
                                        TrieNodeResolverCapability.Hash => GetChild(trieNodeResolver, i),
                                        TrieNodeResolverCapability.Path => GetChild(trieNodeResolver, CollectionsMarshal.AsSpan(trieVisitContext.AbsolutePathNibbles), i),
                                        _ => throw new ArgumentOutOfRangeException()
                                    };
                                    VisitChild(i, child, trieNodeResolver, treeVisitor, visitContext);
                                }
                            }
                        }

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        void VisitMultiThread(ITreeVisitor treeVisitor, ITrieNodeResolver trieNodeResolver, TrieVisitContext visitContext, TrieNode?[] children)
                        {
                            // multi-threaded route
                            Parallel.For(0, BranchesCount, i =>
                            {
                                visitContext.Semaphore.Wait();
                                try
                                {
                                    // we need to have separate context for each thread as context tracks level and branch child index
                                    TrieVisitContext childContext = visitContext.Clone();
                                    if (trieVisitContext.KeepTrackOfAbsolutePath)
                                    {
                                        childContext.AbsolutePathNibbles.Add((byte)i);
                                    }
                                    VisitChild(i, children[i], trieNodeResolver, treeVisitor, childContext);
                                    // no need to remove the element from AbsolutePathNibbles as the childContext is not used in another branch
                                }
                                finally
                                {
                                    visitContext.Semaphore.Release();
                                }
                            });
                        }

                        // visit the current node then increase the Level
                        visitor.VisitBranch(this, trieVisitContext);
                        trieVisitContext.AddVisited();
                        trieVisitContext.Level++;


                        if (trieVisitContext.MaxDegreeOfParallelism != 1 && trieVisitContext.Semaphore.CurrentCount > 1)
                        {
                            // we need to preallocate children
                            TrieNode?[] children = new TrieNode?[BranchesCount];
                            for (int i = 0; i < BranchesCount; i++)
                            {
                                using (trieVisitContext.AbsolutePathNext((byte)i))
                                {
                                    children[i] = nodeResolver.Capability switch
                                    {
                                        TrieNodeResolverCapability.Hash => GetChild(nodeResolver, i),
                                        TrieNodeResolverCapability.Path => GetChild(nodeResolver, CollectionsMarshal.AsSpan(trieVisitContext.AbsolutePathNibbles), i),
                                        _ => throw new ArgumentOutOfRangeException()
                                    };
                                }
                            }

                            if (trieVisitContext.Semaphore.CurrentCount > 1)
                            {
                                VisitMultiThread(visitor, nodeResolver, trieVisitContext, children);
                            }
                            else
                            {
                                VisitSingleThread(visitor, nodeResolver, trieVisitContext);
                            }
                        }
                        else
                        {
                            VisitSingleThread(visitor, nodeResolver, trieVisitContext);
                        }

                        trieVisitContext.Level--;
                        trieVisitContext.BranchChildIndex = null;
                        break;
                    }

                case NodeType.Extension:
                    {
                        visitor.VisitExtension(this, trieVisitContext);
                        trieVisitContext.AddVisited();
                        using (trieVisitContext.AbsolutePathNext(Key!))
                        {
                            TrieNode child = nodeResolver.Capability switch
                            {
                                TrieNodeResolverCapability.Hash => GetChild(nodeResolver, 0),
                                TrieNodeResolverCapability.Path => GetChild(nodeResolver, CollectionsMarshal.AsSpan(trieVisitContext.AbsolutePathNibbles), 0),
                                _ => throw new ArgumentOutOfRangeException()
                            };
                            if (child is null)
                            {
                                throw new InvalidDataException($"Child of an extension {Key} should not be null.");
                            }

                            child.ResolveNode(nodeResolver);
                            child.ResolveKey(nodeResolver, false);
                            if (visitor.ShouldVisit(child.Keccak!))
                            {
                                trieVisitContext.Level++;
                                trieVisitContext.BranchChildIndex = null;
                                child.Accept(visitor, nodeResolver, trieVisitContext);
                                trieVisitContext.Level--;
                            }
                        }
                        break;
                    }

                case NodeType.Leaf:
                    {
                        visitor.VisitLeaf(this, trieVisitContext, Value);
                        trieVisitContext.AddVisited();
                        using (trieVisitContext.AbsolutePathNext(Key!))
                        {
                            if (!trieVisitContext.IsStorage && trieVisitContext.ExpectAccounts) // can combine these conditions
                            {
                                Account account = _accountDecoder.Decode(Value.AsRlpStream());
                                if (account.HasCode && visitor.ShouldVisit(account.CodeHash))
                                {
                                    trieVisitContext.Level++;
                                    trieVisitContext.BranchChildIndex = null;
                                    visitor.VisitCode(account.CodeHash, trieVisitContext);
                                    trieVisitContext.Level--;
                                }

                                if (account.HasStorage && visitor.ShouldVisit(account.StorageRoot))
                                {
                                    trieVisitContext.IsStorage = true;
                                    trieVisitContext.Level++;
                                    trieVisitContext.BranchChildIndex = null;

                                    using (trieVisitContext.AbsolutePathNext(new byte[]{8,0}))
                                    {
                                        if (TryResolveStorageRoot(nodeResolver, CollectionsMarshal.AsSpan(trieVisitContext.AbsolutePathNibbles), out TrieNode? storageRoot))
                                        {
                                            using TrieVisitContext storageTrieVisitContext = new TrieVisitContext
                                            {
                                                // hacky but other solutions are not much better, something nicer would require a bit of thinking
                                                // we introduced a notion of an account on the visit context level which should have no knowledge of account really
                                                // but we know that we have multiple optimizations and assumptions on trees
                                                ExpectAccounts = trieVisitContext.ExpectAccounts,
                                                MaxDegreeOfParallelism = trieVisitContext.MaxDegreeOfParallelism,
                                                KeepTrackOfAbsolutePath = trieVisitContext.KeepTrackOfAbsolutePath
                                            };
                                            storageTrieVisitContext.Level = trieVisitContext.Level;
                                            storageTrieVisitContext.IsStorage = true;
                                            storageTrieVisitContext.BranchChildIndex = null;

                                            storageRoot!.Accept(visitor, nodeResolver, storageTrieVisitContext);
                                        }
                                        else
                                        {
                                            visitor.VisitMissingNode(account.StorageRoot, trieVisitContext);
                                        }
                                    }
                                    trieVisitContext.Level--;
                                    trieVisitContext.IsStorage = false;
                                }
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
