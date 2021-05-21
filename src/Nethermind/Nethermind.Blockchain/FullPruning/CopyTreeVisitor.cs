//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Db.FullPruning;
using Nethermind.Trie;

namespace Nethermind.Blockchain.FullPruning
{
    public class CopyTreeVisitor : ITreeVisitor
    {
        private readonly IPruningContext _pruningContext;
        private readonly CancellationToken _cancellationToken;
        private long _persistedNodes = 0;

        public CopyTreeVisitor(IPruningContext pruningContext, CancellationToken cancellationToken)
        {
            _pruningContext = pruningContext;
            _cancellationToken = cancellationToken;
        }

        public bool ShouldVisit(Keccak nextNode) => !_cancellationToken.IsCancellationRequested;

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext) { }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            // TODO: Cancel?
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(node);

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext) => PersistNode(node);

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null) => PersistNode(node);

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext) { }
        
        private void PersistNode(TrieNode node)
        {
            _pruningContext[node.Keccak!.Bytes] = node.FullRlp;
            _persistedNodes++;
        }
    }
}
