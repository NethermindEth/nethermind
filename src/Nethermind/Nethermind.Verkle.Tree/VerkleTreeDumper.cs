// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree;

public class VerkleTreeDumper : IVerkleTreeVisitor
{

    private readonly StringBuilder _builder = new StringBuilder();

    public void Reset()
    {
        _builder.Clear();
    }

    public bool ShouldVisit(byte[] nextNode)
    {
        return true;
    }
    public void VisitTree(VerkleCommitment rootHash, TrieVisitContext trieVisitContext)
    {
        if (rootHash.Equals(VerkleCommitment.Zero))
        {
            _builder.AppendLine("EMPTY TREE");
        }
        else
        {
            _builder.AppendLine("STATE TREE");
        }
    }

    private string GetPrefix(TrieVisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : string.Empty, $"{GetChildIndex(context)}");
    private string GetIndent(int level) => new('+', level * 2);
    private string GetChildIndex(TrieVisitContext context) => context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";

    public void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine($"{GetIndent(trieVisitContext.Level)}{GetChildIndex(trieVisitContext)}MISSING {nodeKey}");
    }
    public void VisitBranchNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine($"{GetPrefix(trieVisitContext)}InternalNode | -> Key: {trieVisitContext.AbsolutePathIndex.ToArray().ToHexString()}");
    }
    public void VisitStemNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        _builder.AppendLine($"{GetPrefix(trieVisitContext)}STEM | -> Key: {trieVisitContext.AbsolutePathIndex.ToArray().ToHexString()}");
    }
    public void VisitLeafNode(ReadOnlySpan<byte> nodeKey, TrieVisitContext trieVisitContext, byte[]? nodeValue)
    {
        _builder.AppendLine($"{GetPrefix(trieVisitContext)}LEAF | -> Key: {nodeKey.ToHexString()}  Value: {nodeValue.ToHexString()}");
    }

    public override string ToString()
    {
        return _builder.ToString();
    }
}
