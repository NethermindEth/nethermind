// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Analytics
{
    public class SupplyVerifier : ITreeVisitor
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

        public bool ShouldVisit(Hash256 nextNode)
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

        public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            _logger.Warn($"Missing node {nodeHash}");
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
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

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            _nodesVisited++;
            if (trieVisitContext.IsStorage)
            {
                _ignoreThisOne.Add(node.GetChildHash(0));
            }
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
            _nodesVisited++;

            if (trieVisitContext.IsStorage)
            {
                return;
            }

            AccountDecoder accountDecoder = new AccountDecoder();
            Account account = accountDecoder.Decode(node.Value.AsRlpStream());
            Balance += account.Balance;
            _accountsVisited++;

            _logger.Info($"Balance after visiting {_accountsVisited} accounts and {_nodesVisited} nodes: {Balance}");
        }

        public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
            _nodesVisited++;
        }
    }
}
