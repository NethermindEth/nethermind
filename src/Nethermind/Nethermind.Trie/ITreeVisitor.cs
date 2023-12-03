// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public interface ITreeVisitor
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

        void VisitLeaf(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null);

        void VisitCode(in TreePath path, Hash256 codeHash, TrieVisitContext trieVisitContext);
    }
}
