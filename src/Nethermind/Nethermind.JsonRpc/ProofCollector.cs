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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
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
        private int _pathIndex = 0;
        
        private readonly Address _address;

        public ProofCollector(Address address)
        {
            _address = address;
        }

        private AccountProof _accountProof;

        public AccountProof BuildResult()
        {
            _accountProof.Proof = _proof.ToArray();
            return _accountProof;
        }

        public bool ShouldVisit(Keccak nextNode)
        {
            return _visitingFilter.Contains(nextNode);
        }

        public void VisitTree(Keccak rootHash, VisitContext context)
        {
            _accountProof = new AccountProof();
        }

        public void VisitMissingNode(Keccak nodeHash, VisitContext context)
        {
        }

        private List<byte[]> _proof = new List<byte[]>();
        
        private Nibble[] _prefix => Nibbles.FromBytes(Keccak.Compute(_address.Bytes).Bytes);
        
        public void VisitBranch(TrieNode node, VisitContext context)
        {
            _visitingFilter.Clear();
            _proof.Add(node.FullRlp.Bytes);
            _visitingFilter.Add(node.GetChildHash((byte) _prefix[_pathIndex]));
            _pathIndex++;
        }

        private HashSet<Keccak> _visitingFilter = new HashSet<Keccak>();
        
        public void VisitExtension(TrieNode node, VisitContext context)
        {
            _visitingFilter.Clear();
            _proof.Add(node.FullRlp.Bytes);
            _visitingFilter.Add(node.GetChildHash(0));
            _pathIndex += node.Path.Length;
        }

        public void VisitLeaf(TrieNode node, VisitContext context)
        {
            _visitingFilter.Clear();
            _proof.Add(node.FullRlp.Bytes);
            if (context.IsStorage)
            {
            }
            else
            {
                Account account = _accountDecoder.Decode(new Rlp.DecoderContext(node.Value));
                _accountProof.Nonce = account.Nonce;
                _accountProof.Balance = account.Balance;
                _accountProof.StorageRoot = account.StorageRoot;
                _accountProof.CodeHash = account.CodeHash;
            }
            
            _pathIndex += node.Path.Length;
            _visitingFilter.Add(_accountProof.StorageRoot);
        }
        
        private AccountDecoder _accountDecoder = new AccountDecoder();

        public void VisitCode(Keccak codeHash, byte[] code, VisitContext context)
        {
            throw new InvalidOperationException($"{nameof(ProofCollector)} does never expect to visit code");
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