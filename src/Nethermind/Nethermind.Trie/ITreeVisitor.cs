// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        /// <summary>
        /// Used by snap sync, specify that a range of nodes in increasing order will be traversed. This turn on
        /// some optimization for this specific scenario.
        /// </summary>
        public bool IsRangeScan => IsFullDbScan;

        /// <summary>
        /// Specify that the account will be decoded and code and storage will get traversed.
        /// </summary>
        public bool ExpectAccounts => true;

        /// <summary>
        /// Extra read flags for passing to triestore. Used to optimize snap sync's gettrie so that it won't effect
        /// block processing.
        /// </summary>
        ReadFlags ExtraReadFlag => ReadFlags.None;

        bool ShouldVisit(in TNodeContext nodeContext, in ValueHash256 nextNode);

        void VisitTree(in TNodeContext nodeContext, in ValueHash256 rootHash);

        void VisitMissingNode(in TNodeContext nodeContext, in ValueHash256 nodeHash);

        void VisitBranch(in TNodeContext nodeContext, TrieNode node);

        void VisitExtension(in TNodeContext nodeContext, TrieNode node);

        void VisitLeaf(in TNodeContext nodeContext, TrieNode node);

        /// <summary>
        /// Called right after VisitLeaf when `ExpectAccounts` is true.
        /// The visitor need to decode account to traverse storage. So if you need the account instead of decoding from
        /// `VisitLeaf` you might as well get it here.
        /// </summary>
        void VisitAccount(in TNodeContext nodeContext, TrieNode node, in AccountStruct account);
    }
}
