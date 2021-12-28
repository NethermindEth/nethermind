//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Runtime.CompilerServices;
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
        private static readonly ParallelOptions _defaultOptions = new();
        private const int BranchesCount = 16;
            
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
                    void VisitChild(int i, TrieNode? child, ITrieNodeResolver resolver, ITreeVisitor v, TrieVisitContext context)
                    {
                        if (child != null)
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

                    visitor.VisitBranch(this, trieVisitContext);
                    trieVisitContext.Level++;
                    if (trieVisitContext.MaxDegreeOfParallelism != 1)
                    {
                        TrieVisitContext GetChildContext(TrieVisitContext context)
                        {
                            TrieVisitContext childContext = context.Clone();
                            int maxDegreeOfParallelism = context.MaxDegreeOfParallelism;
                            childContext.MaxDegreeOfParallelism =
                                maxDegreeOfParallelism > 1
                                    ? Math.Max(1, maxDegreeOfParallelism / BranchesCount)
                                    : maxDegreeOfParallelism;
                            return childContext;
                        }
                        
                        TrieNode?[] children = new TrieNode?[BranchesCount];
                        for (int i = 0; i < BranchesCount; i++)
                        {
                            children[i] = GetChild(nodeResolver, i);
                        }

                        ParallelOptions options = trieVisitContext.MaxDegreeOfParallelism == 0 ? _defaultOptions : new ParallelOptions() {MaxDegreeOfParallelism = trieVisitContext.MaxDegreeOfParallelism % (BranchesCount + 1)};
                        Parallel.For(0, BranchesCount, options, i => VisitChild(i, children[i], nodeResolver, visitor, GetChildContext(trieVisitContext)));
                    }
                    else
                    {
                        for (int i = 0; i < BranchesCount; i++)
                        {
                            VisitChild(i, GetChild(nodeResolver, i), nodeResolver, visitor, trieVisitContext);
                        }
                    }

                    trieVisitContext.Level--;
                    trieVisitContext.BranchChildIndex = null;
                    break;
                }

                case NodeType.Extension:
                {
                    visitor.VisitExtension(this, trieVisitContext);
                    TrieNode child = GetChild(nodeResolver, 0);
                    if (child == null)
                    {
                        throw new InvalidDataException($"Child of an extension {Key} should not be null.");
                    }
                    
                    child.ResolveKey(nodeResolver, false);
                    if (visitor.ShouldVisit(child.Keccak!))
                    {
                        trieVisitContext.Level++;
                        trieVisitContext.BranchChildIndex = null;
                        child.Accept(visitor, nodeResolver, trieVisitContext);
                        trieVisitContext.Level--;
                    }

                    break;
                }

                case NodeType.Leaf:
                {
                    visitor.VisitLeaf(this, trieVisitContext, Value);
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
                            TrieNode storageRoot = new(NodeType.Unknown, account.StorageRoot);
                            trieVisitContext.Level++;
                            trieVisitContext.BranchChildIndex = null;
                            storageRoot.Accept(visitor, nodeResolver, trieVisitContext);
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
