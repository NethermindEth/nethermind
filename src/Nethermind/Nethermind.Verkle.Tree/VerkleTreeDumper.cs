// // SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only
//
using System;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public class VerkleTreeDumper : IVerkleTreeVisitor<VerklePathContext>
{
    private readonly StringBuilder _builder = new();

    public bool ShouldVisit(byte[] nextNode)
    {
        return true;
    }

    public void Reset()
    {
        _builder.Clear();
    }


    //////////
    /// ////////////
    /// ////////////////
    /// ///////////////
    ///
    ///
    public bool IsFullDbScan { get; } = true;

    public void VisitTree(in VerklePathContext nodeContext, in ValueHash256 rootHash)
    {
        _builder.AppendLine(rootHash.Equals(Hash256.Zero) ? "EMPTY TREE" : "STATE TREE");

    }

    public void VisitMissingNode(in VerklePathContext nodeContext, byte[] nodeKey)
    {
        _builder.AppendLine($"{GetIndent(nodeContext.Level)}{GetChildIndex(nodeContext)}MISSING {nodeKey}");
    }

    public void VisitBranchNode(in VerklePathContext nodeContext, InternalNode node)
    {
        _builder.AppendLine(
            $"{GetPrefix(nodeContext)}INTN | -> Key: {nodeContext.Path.ToHexString()}");

    }

    public void VisitStemNode(in VerklePathContext nodeContext, InternalNode node)
    {
        _builder.AppendLine(
            $"{GetPrefix(nodeContext)}STEM | -> Key: {nodeContext.Path.ToHexString()}");

    }

    public void VisitLeafNode(in VerklePathContext nodeContext, ReadOnlySpan<byte> nodeKey, byte[]? nodeValue)
    {
        _builder.AppendLine(
            $"{GetPrefix(nodeContext)}LEAF | -> Key: {nodeKey.ToHexString()}  Value: {nodeValue.ToHexString()}");

    }

    private static string GetPrefix(in VerklePathContext context) => string.Concat($"{GetIndent(context.Level)}", string.Empty, $"{GetChildIndex(context)}");
    private static string GetIndent(int level) => new('+', level * 2);
    private static string GetChildIndex(in VerklePathContext context) => context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";

    public override string ToString()
    {
        return _builder.ToString();
    }
}
