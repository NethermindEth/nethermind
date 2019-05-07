/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.JsonRpc
{
    /// <summary>
    ///{
    ///  "id": 1,
    ///  "jsonrpc": "2.0",
    ///  "method": "eth_getProof",
    ///  "params": [
    ///    "0x7F0d15C7FAae65896648C8273B6d7E43f58Fa842",
    ///    [  "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421" ],
    ///    "latest"
    ///  ]
    ///}
    ///  
    ///{
    ///  "id": 1,
    ///  "jsonrpc": "2.0",
    ///  "result": {
    ///    "accountProof": [
    ///    "0xf90211a...0701bc80",
    ///    "0xf90211a...0d832380",
    ///    "0xf90211a...5fb20c80",
    ///    "0xf90211a...0675b80",
    ///    "0xf90151a0...ca08080"
    ///    ],
    ///  "balance": "0x0",
    ///  "codeHash": "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470",
    ///  "nonce": "0x0",
    ///  "storageHash": "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421",
    ///  "storageProof": [
    ///  {
    ///    "key": "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421",
    ///    "proof": [
    ///    "0xf90211a...0701bc80",
    ///    "0xf90211a...0d832380"
    ///    ],
    ///    "value": "0x1"
    ///  }
    ///  ]
    ///  }
    ///}
    /// </summary>
    public class ProofCollector : ITreeVisitor
    {
        private Keccak _nextNodeToVisit;
        
        private readonly Address _address;

        public ProofCollector(Address address)
        {
            _address = address;
        }
        
        public AccountProof AccountProof { get; set; }

        public bool ShouldVisit(Keccak nextNode)
        {
            return true;
        }

        public void VisitTree(Keccak rootHash, VisitContext context)
        {
            AccountProof = new AccountProof();
        }

        public void VisitMissingNode(Keccak nodeHash, VisitContext context)
        {
        }

        public void VisitBranch(byte[] hashOrRlp, VisitContext context)
        {
            TrieNode node = new TrieNode(NodeType.Branch, new Rlp(hashOrRlp));
        }

        public void VisitExtension(byte[] hashOrRlp, VisitContext context)
        {
            TrieNode node = new TrieNode(NodeType.Extension, new Rlp(hashOrRlp));
        }

        public void VisitLeaf(byte[] hashOrRlp, VisitContext context)
        {
            TrieNode node = new TrieNode(NodeType.Leaf, new Rlp(hashOrRlp));
            
            if (context.IsStorage)
            {
            }
            else
            {
                new TrieNode(NodeType.Unknown, new Rlp(hashOrRlp));
            }
        }

        public void VisitCode(Keccak codeHash, byte[] code, VisitContext context)
        {
        }
    }

    public class AccountProof
    {
        public byte[][] Proof { get; set; }
        public UInt256 Balance { get; set; }
        public Keccak CodeHash { get; set; }
        
        public UInt256 Nonce { get; set; }
        public Keccak StorageRoot { get; set; }
        public StorageProof[] StorageProofs { get; set; }
    }

    public class StorageProof
    {
        public byte[][] Proof { get; set; }
        public Keccak Key { get; set; }
        public byte[] Value { get; set; }
    }
}