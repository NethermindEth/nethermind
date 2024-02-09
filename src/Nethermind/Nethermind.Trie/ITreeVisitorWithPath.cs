// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

// TODO: Break this.
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

    public ITreeVisitor<PathCtx> ToContextualTreeVisitor()
    {
        return new TreeVisitorAdaptor(this);
    }

    public struct PathCtx : INodeContext<PathCtx>
    {
        public TreePath Path = TreePath.Empty;

        public PathCtx()
        {
        }

        public PathCtx Add(byte[] nibblePath)
        {
            return new PathCtx()
            {
                Path = Path.Append(nibblePath)
            };
        }

        public PathCtx Add(byte nibble)
        {
            return new PathCtx()
            {
                Path = Path.Append(nibble)
            };
        }

        public PathCtx AddStorage(in ValueHash256 storage)
        {
            return new PathCtx();
        }
    }

    private class TreeVisitorAdaptor : ITreeVisitor<PathCtx>
    {
        private ITreeVisitorWithPath _treeVisitorImplementation;

        public TreeVisitorAdaptor(ITreeVisitorWithPath treeVisitorImplementation)
        {
            _treeVisitorImplementation = treeVisitorImplementation;
        }

        public bool IsFullDbScan => _treeVisitorImplementation.IsFullDbScan;

        public ReadFlags ExtraReadFlag => _treeVisitorImplementation.ExtraReadFlag;

        public bool ShouldVisit(in PathCtx nodeContext, Hash256 nextNode)
        {
            return _treeVisitorImplementation.ShouldVisit(nextNode);
        }

        public void VisitTree(in PathCtx nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
            _treeVisitorImplementation.VisitTree(rootHash, trieVisitContext);
        }

        public void VisitMissingNode(in PathCtx nodeContext, Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            _treeVisitorImplementation.VisitMissingNode(in nodeContext.Path, nodeHash, trieVisitContext);
        }

        public void VisitBranch(in PathCtx nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
        {
            _treeVisitorImplementation.VisitBranch(in nodeContext.Path, node, trieVisitContext);
        }

        public void VisitExtension(in PathCtx nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
        {
            _treeVisitorImplementation.VisitExtension(in nodeContext.Path, node, trieVisitContext);
        }

        public void VisitLeaf(in PathCtx nodeContext, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
            _treeVisitorImplementation.VisitLeaf(in nodeContext.Path, node, trieVisitContext, value);
        }

        public void VisitCode(in PathCtx nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
            _treeVisitorImplementation.VisitCode(in nodeContext.Path, codeHash, trieVisitContext);
        }
    }
}
