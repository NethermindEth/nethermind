// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    public void PrintLeaves(long block)
    {
        // string docPath = "/home/eurus/";
        // using var outputFile = new StreamWriter(Path.Combine(docPath, "WriteLines.txt"), true);
        //
        // foreach (PathWithSubTree data in VerkleStateStore.GetLeafRangeIterator(Stem.Zero, Stem.MaxValue, root, long.MaxValue))
        // {
        //     byte[] stem = new byte[32];
        //     data.Path.BytesAsSpan.CopyTo(stem);
        //
        //     foreach (var inner in data.SubTree)
        //     {
        //         stem[31] = inner.SuffixByte;
        //         outputFile.WriteLine($"{stem.ToHexString()}: {inner.Leaf.ToHexString()}");
        //     }
        // }

        VerkleStateStore.DumpTree(block, TreeCache);

    }

    public bool HasStateForStateRoot(Hash256 stateRoot)
    {
        return VerkleStateStore.HasStateForBlock(stateRoot);
    }

    public void Accept<TNodeContext>(IVerkleTreeVisitor<TNodeContext> visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
        where TNodeContext : struct, INodeContext<TNodeContext>
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ArgumentNullException.ThrowIfNull(rootHash);
        visitingOptions ??= VisitingOptions.Default;

        using TrieVisitContext trieVisitContext = new()
        {
            MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
        };

        if (!rootHash.Equals(new Hash256(Keccak.EmptyTreeHash.Bytes.ToArray())))
            VerkleStateStore.MoveToStateRoot(rootHash);
        else
            return;

        ReadFlags flags = visitor.ExtraReadFlag;
        if (visitor.IsFullDbScan)
        {
            // With halfpath or flat, the nodes are ordered so readahead will make things faster.
            flags |= ReadFlags.HintReadAhead;

        }

        visitor.VisitTree(default, rootHash);

        VerklePath emptyPath = VerklePath.Empty;
        InternalNode rootNode = VerkleStateStore.GetInternalNode([]);

        if (rootNode is not null) RecurseNodes(visitor, default, ref emptyPath, rootNode, trieVisitContext);
        else visitor.VisitMissingNode(default, []);
    }



    private void RecurseNodes<TNodeContext>(IVerkleTreeVisitor<TNodeContext> visitor, in TNodeContext nodeContext, ref VerklePath path, InternalNode node, TrieVisitContext trieVisitContext)
        where TNodeContext : struct, INodeContext<TNodeContext>
    {
        switch (node.NodeType)
        {
            case VerkleNodeType.BranchNode:
                {
                    visitor.VisitBranchNode(nodeContext, node);
                    path.AppendMut(0);
                    for (var i = 0; i < 256; i++)
                    {
                        path.SetLast((byte)i);
                        InternalNode? childNode =
                            VerkleStateStore.GetInternalNode(path.ToPath());
                        TNodeContext childContext = nodeContext.Add((byte)i);
                        if (childNode is not null && visitor.ShouldVisit(path.ToPath().ToArray()))
                            RecurseNodes(visitor, childContext, ref path, childNode!, trieVisitContext);
                    }
                    path.TruncateOne();
                    break;
                }
            case VerkleNodeType.StemNode:
                {
                    visitor.VisitStemNode(nodeContext, node);
                    Stem stemKey = node.Stem;
                    Span<byte> childKey = stackalloc byte[32];
                    stemKey.Bytes.CopyTo(childKey);
                    for (var i = 0; i < 256; i++)
                    {
                        childKey[31] = (byte)i;
                        var childNode = VerkleStateStore.GetLeaf(childKey.ToArray());
                        if (childNode is not null && visitor.ShouldVisit(childKey.ToArray()))
                            visitor.VisitLeafNode(nodeContext, childKey.ToArray(), childNode);
                    }
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}

public struct VerklePathContext(int level, int? branchChildIndex) : INodeContext<VerklePathContext>
{
    public readonly int Level = level;
    public VerklePath Path = VerklePath.Empty;
    public readonly int? BranchChildIndex = branchChildIndex;

    public VerklePathContext Add(ReadOnlySpan<byte> nibblePath)
    {
        return new VerklePathContext(Level + 1, null)
        {
            Path = Path.Append(nibblePath)
        };
    }

    public VerklePathContext Add(byte nibble)
    {
        return new VerklePathContext(Level + 1, null)
        {
            Path = Path.Append(nibble)
        };
    }

    public readonly VerklePathContext AddStorage(in ValueHash256 storage)
    {
        return new VerklePathContext(Level + 1, null);
    }
}
