// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Analytics
{
    public class SupplyVerifier : ITreeVisitor<OldStyleTrieVisitContext>
    {
        private readonly ILogger _logger;
        private readonly HashSet<Hash256> _ignoreThisOne = new HashSet<Hash256>();
        private int _accountsVisited;
        private int _nodesVisited;

        public SupplyVerifier(ILogger logger)
        {
            _logger = logger;
        }

        public UInt256 Balance { get; set; } = UInt256.Zero;

        public bool IsFullDbScan => false;

        public bool ShouldVisit(in OldStyleTrieVisitContext _, Hash256 nextNode)
        {
            if (_ignoreThisOne.Count > 16)
            {
                _logger.Warn($"Ignore count leak -> {_ignoreThisOne.Count}");
            }

            if (_ignoreThisOne.Remove(nextNode))
            {
                return false;
            }

            return true;
        }

        public void VisitTree(in OldStyleTrieVisitContext _, Hash256 rootHash)
        {
        }

        public void VisitMissingNode(in OldStyleTrieVisitContext _, Hash256 nodeHash)
        {
            _logger.Warn($"Missing node {nodeHash}");
        }

        public void VisitBranch(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
        {
            _logger.Info($"Balance after visiting {_accountsVisited} accounts and {_nodesVisited} nodes: {Balance}");
            _nodesVisited++;

            if (trieVisitContext.IsStorage)
            {
                for (int i = 0; i < 16; i++)
                {
                    Hash256 childHash = node.GetChildHash(i);
                    if (childHash is not null)
                    {
                        _ignoreThisOne.Add(childHash);
                    }
                }
            }
        }

        public void VisitExtension(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
        {
            _nodesVisited++;
            if (trieVisitContext.IsStorage)
            {
                _ignoreThisOne.Add(node.GetChildHash(0));
            }
        }

        public void VisitLeaf(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
        {
        }

        public void VisitAccount(in OldStyleTrieVisitContext _, TrieNode node, in AccountStruct account)
        {
            _nodesVisited++;
            Balance += account.Balance;
            _accountsVisited++;

            _logger.Info($"Balance after visiting {_accountsVisited} accounts and {_nodesVisited} nodes: {Balance}");
        }
    }
}
