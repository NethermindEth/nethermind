// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public class VerkleTreeDumper : IVerkleTreeVisitor
{
    private readonly StringBuilder _builder = new();

    public bool ShouldVisit(byte[] nextNode)
    {
        return true;
    }

    public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine(rootHash.Equals(Hash256.Zero) ? "EMPTY TREE" : "STATE TREE");
    }

    public void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine($"{GetIndent(trieVisitContext.Level)}{GetChildIndex(trieVisitContext)}MISSING {nodeKey}");
    }

    public void VisitBranchNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine(
            $"{GetPrefix(trieVisitContext)}InternalNode | -> Key: {trieVisitContext.AbsolutePathIndex.ToArray().ToHexString()}");
    }

    public void VisitStemNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine(
            $"{GetPrefix(trieVisitContext)}STEM | -> Key: {trieVisitContext.AbsolutePathIndex.ToArray().ToHexString()}");
    }

    public void VisitLeafNode(ReadOnlySpan<byte> nodeKey, TrieVisitContext trieVisitContext, byte[]? nodeValue)
    {
        _builder.AppendLine(
            $"{GetPrefix(trieVisitContext)}LEAF | -> Key: {nodeKey.ToHexString()}  Value: {nodeValue.ToHexString()}");
    }

    public void Reset()
    {
        _builder.Clear();
    }

    private string GetPrefix(TrieVisitContext context)
    {
        return string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : string.Empty,
            $"{GetChildIndex(context)}");
    }

    private string GetIndent(int level)
    {
        return new string('+', level * 2);
    }

    private string GetChildIndex(TrieVisitContext context)
    {
        return context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";
    }

    public override string ToString()
    {
        return _builder.ToString();
    }
}
