// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    public void Accept(ITreeVisitor visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
        visitingOptions ??= VisitingOptions.Default;

        using var trieVisitContext = new TrieVisitContext
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
            // TODO: use another way to check if we have the state root - this is not good. - move to the stateStore
            //   something of sorts HasStateForStateRoot(Keccak stateRoot)
            _logger.Info($"using the state store: {_verkleStateStore.GetType()}");
            _verkleStateStore.MoveToStateRoot(new Hash256(rootHash.Bytes.ToArray()));
        }
        else
        {
            return;
        }

        throw new Exception();
    }

    public void Accept(IVerkleTreeVisitor visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
        visitingOptions ??= VisitingOptions.Default;

        using TrieVisitContext trieVisitContext = new()
        {
            // hacky but other solutions are not much better, something nicer would require a bit of thinking
            // we introduced a notion of an account on the visit context level which should have no knowledge of account really
            // but we know that we have multiple optimizations and assumptions on trees
            ExpectAccounts = visitingOptions.ExpectAccounts,
            MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
            KeepTrackOfAbsolutePath = true
        };

        if (!rootHash.Equals(new Hash256(Keccak.EmptyTreeHash.Bytes.ToArray())))
            _verkleStateStore.MoveToStateRoot(rootHash);
        else
            return;

        visitor.VisitTree(rootHash, trieVisitContext);

        RecurseNodes(visitor, _verkleStateStore.GetInternalNode(Array.Empty<byte>()), trieVisitContext);
    }

    public bool HasStateForStateRoot(Hash256 stateRoot)
    {
        return _verkleStateStore.HasStateForBlock(stateRoot);
    }

    private void RecurseNodes(IVerkleTreeVisitor visitor, InternalNode node, TrieVisitContext trieVisitContext)
    {
        switch (node.NodeType)
        {
            case VerkleNodeType.BranchNode:
            {
                visitor.VisitBranchNode(node, trieVisitContext);
                trieVisitContext.Level++;
                for (var i = 0; i < 256; i++)
                {
                    trieVisitContext.AbsolutePathIndex.Add((byte)i);
                    InternalNode? childNode =
                        _verkleStateStore.GetInternalNode(trieVisitContext.AbsolutePathIndex.ToArray());
                    if (childNode is not null && visitor.ShouldVisit(trieVisitContext.AbsolutePathIndex.ToArray()))
                        RecurseNodes(visitor, childNode!, trieVisitContext);
                    trieVisitContext.AbsolutePathIndex.RemoveAt(trieVisitContext.AbsolutePathIndex.Count - 1);
                }

                trieVisitContext.Level--;
                break;
            }
            case VerkleNodeType.StemNode:
            {
                visitor.VisitStemNode(node, trieVisitContext);
                Stem stemKey = node.Stem;
                Span<byte> childKey = stackalloc byte[32];
                stemKey.Bytes.CopyTo(childKey);
                trieVisitContext.Level++;
                for (var i = 0; i < 256; i++)
                {
                    childKey[31] = (byte)i;
                    var childNode = _verkleStateStore.GetLeaf(childKey.ToArray());
                    if (childNode is not null && visitor.ShouldVisit(childKey.ToArray()))
                        visitor.VisitLeafNode(childKey.ToArray(), trieVisitContext, childNode);
                }

                trieVisitContext.Level--;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
