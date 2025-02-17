// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public interface ITreeVisitor<TNodeContext>
        where TNodeContext : struct, INodeContext<TNodeContext>
    {
        /// <summary>
        /// Specify that this is a full table scan and should optimize for it.
        /// </summary>
        public bool IsFullDbScan { get; }

        public bool IsRangeScan => IsFullDbScan;

        ReadFlags ExtraReadFlag => ReadFlags.None;

        bool ShouldVisit(in TNodeContext nodeContext, Hash256 nextNode);

        void VisitTree(in TNodeContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext);

        void VisitMissingNode(in TNodeContext nodeContext, Hash256 nodeHash, TrieVisitContext trieVisitContext);

        void VisitBranch(in TNodeContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext);

        void VisitExtension(in TNodeContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext);

        void VisitLeaf(in TNodeContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value);

        void VisitCode(in TNodeContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext);
    }
}
