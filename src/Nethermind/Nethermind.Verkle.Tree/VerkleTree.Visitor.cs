// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
        visitingOptions ??= VisitingOptions.Default;

        using TrieVisitContext trieVisitContext = new TrieVisitContext
        {
            // hacky but other solutions are not much better, something nicer would require a bit of thinking
            // we introduced a notion of an account on the visit context level which should have no knowledge of account really
            // but we know that we have multiple optimizations and assumptions on trees
            ExpectAccounts = visitingOptions.ExpectAccounts,
            MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
            KeepTrackOfAbsolutePath = true
        };

        if (!rootHash.Equals(Keccak.EmptyTreeHash))
        {
            _stateDb.MoveToStateRoot(rootHash.Bytes);
        }
        else
        {
            return;
        }

        if (visitor is RootCheckVisitor)
        {
            if (!rootHash.Bytes.SequenceEqual(_stateDb.GetStateRoot())) visitor.VisitMissingNode(Keccak.Zero, trieVisitContext);
        }
        else
        {
            throw new Exception();
        }

    }

    public void Accept(IVerkleTreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
        visitingOptions ??= VisitingOptions.Default;

        using TrieVisitContext trieVisitContext = new TrieVisitContext
        {
            // hacky but other solutions are not much better, something nicer would require a bit of thinking
            // we introduced a notion of an account on the visit context level which should have no knowledge of account really
            // but we know that we have multiple optimizations and assumptions on trees
            ExpectAccounts = visitingOptions.ExpectAccounts,
            MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
            KeepTrackOfAbsolutePath = true
        };

        if (!rootHash.Equals(Keccak.EmptyTreeHash))
        {
            _stateDb.MoveToStateRoot(rootHash.Bytes);
        }
        else
        {
            return;
        }

        visitor.VisitTree(rootHash.Bytes, trieVisitContext);

        RecurseNodes(visitor, _stateDb.GetBranch(Array.Empty<byte>()), trieVisitContext);

    }

    private void RecurseNodes(IVerkleTreeVisitor visitor, InternalNode node, TrieVisitContext trieVisitContext)
    {
        switch (node.NodeType)
        {
            case Nodes.NodeType.BranchNode:
                {
                    visitor.VisitBranchNode((BranchNode)node, trieVisitContext);
                    trieVisitContext.Level++;
                    for (int i = 0; i < 256; i++)
                    {
                        trieVisitContext.AbsolutePathIndex.Add((byte)i);
                        InternalNode? childNode = _stateDb.GetBranch(trieVisitContext.AbsolutePathIndex.ToArray());
                        if (childNode is not null && visitor.ShouldVisit(trieVisitContext.AbsolutePathIndex.ToArray()))
                        {
                            RecurseNodes(visitor, childNode!, trieVisitContext);
                        }
                        trieVisitContext.AbsolutePathIndex.RemoveAt(trieVisitContext.AbsolutePathIndex.Count - 1);
                    }
                    trieVisitContext.Level--;
                    break;
                }
            case Nodes.NodeType.StemNode:
                {
                    visitor.VisitStemNode((StemNode)node, trieVisitContext);
                    byte[] stemKey = node.Stem;
                    Span<byte> childKey = stackalloc byte[32];
                    stemKey.CopyTo(childKey);
                    trieVisitContext.Level++;
                    for (int i = 0; i < 256; i++)
                    {
                        childKey[31] = (byte)i;
                        byte[]? childNode = _stateDb.GetLeaf(childKey.ToArray());
                        if (childNode is not null && visitor.ShouldVisit(childKey.ToArray()))
                        {
                            visitor.VisitLeafNode(childKey.ToArray(), trieVisitContext, childNode);
                        }
                    }
                    trieVisitContext.Level--;
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
