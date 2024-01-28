// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public interface ITreeVisitorWithPath
{
    /// <summary>
    /// Specify that this is a full table scan and should optimize for it.
    /// </summary>
    public bool IsFullDbScan { get; }

    ReadFlags ExtraReadFlag => ReadFlags.None;

    bool ShouldVisit(Hash256 nextNode);

    void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext);

    void VisitMissingNode(in TreePath path, Hash256 nodeHash, TrieVisitContext trieVisitContext);

    void VisitBranch(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext);

    void VisitExtension(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext);

    void VisitLeaf(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value);

    void VisitCode(in TreePath path, Hash256 codeHash, TrieVisitContext trieVisitContext);

    public static ITreeVisitorWithPath FromITreeVisitor(ITreeVisitor treeVisitor)
    {
        return new TreeVisitorAdaptor(treeVisitor);
    }

    private class TreeVisitorAdaptor : ITreeVisitorWithPath
    {
        private ITreeVisitor _treeVisitor;
        public bool IsFullDbScan => _treeVisitor.IsFullDbScan;

        internal TreeVisitorAdaptor(ITreeVisitor treeVisitor)
        {
            _treeVisitor = treeVisitor;
        }

        public bool ShouldVisit(Hash256 nextNode)
        {
            return _treeVisitor.ShouldVisit(nextNode);
        }

        public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
            _treeVisitor.VisitTree(rootHash, trieVisitContext);
        }

        public void VisitMissingNode(in TreePath path, Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            _treeVisitor.VisitMissingNode(nodeHash, trieVisitContext);
        }

        public void VisitBranch(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext)
        {
            _treeVisitor.VisitBranch(node, trieVisitContext);
        }

        public void VisitExtension(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext)
        {
            _treeVisitor.VisitExtension(node, trieVisitContext);
        }

        public void VisitLeaf(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
            _treeVisitor.VisitLeaf(node, trieVisitContext, value);
        }

        public void VisitCode(in TreePath path, Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
            _treeVisitor.VisitCode(codeHash, trieVisitContext);
        }
    }
}
