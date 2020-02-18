//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Store.Proofs
{
    /// <summary>
    /// EIP-1186 style proof collector
    /// </summary>
    public class AccountProofCollector : ITreeVisitor
    {
        private int _pathIndex;
        private Address _address;
        private AccountProof _accountProof;

        private Nibble[] _prefix => Nibbles.FromBytes(Keccak.Compute(_address.Bytes).Bytes);
        private Nibble[][] _storagePrefixes;

        private List<byte[]> _proofBits = new List<byte[]>();
        private List<byte[]>[] _storageProofBits;

        private Dictionary<Keccak, NodeInfo> _nodeInfos = new Dictionary<Keccak, NodeInfo>();
        private HashSet<Keccak> _visitingFilter = new HashSet<Keccak>();

        private class NodeInfo
        {
            public NodeInfo()
            {
                StorageIndices = new List<int>();
            }

            public int PathIndex { get; set; }
            public List<int> StorageIndices { get; set; }
        }

        private static Keccak ToKey(byte[] index)
        {
            return Keccak.Compute(index);
        }

        private static byte[] ToKey(UInt256 index)
        {
            byte[] bytes = new byte[32];
            index.ToBigEndian(bytes);
            return bytes;
        }

        public AccountProofCollector(Address address, UInt256[] storageKeys)
            : this(address, storageKeys.Select(ToKey).ToArray())
        {
        }

        public AccountProofCollector(Address address, params byte[][] storageKeys)
        {
            storageKeys ??= new byte[0][];
            _address = address ?? throw new ArgumentNullException(nameof(address));
            Keccak[] localStorageKeys = storageKeys.Select(ToKey).ToArray();

            _accountProof = new AccountProof();
            _accountProof.StorageProofs = new StorageProof[localStorageKeys.Length];
            _accountProof.Address = _address;

            _storagePrefixes = new Nibble[localStorageKeys.Length][];
            _storageProofBits = new List<byte[]>[localStorageKeys.Length];
            for (int i = 0; i < _storageProofBits.Length; i++)
            {
                _storageProofBits[i] = new List<byte[]>();
            }

            for (int i = 0; i < localStorageKeys.Length; i++)
            {
                _storagePrefixes[i] = Nibbles.FromBytes(localStorageKeys[i].Bytes);

                _accountProof.StorageProofs[i] = new StorageProof();
                _accountProof.StorageProofs[i].Key = storageKeys[i];
                _accountProof.StorageProofs[i].Value = null;
            }
        }

        public AccountProof BuildResult()
        {
            _accountProof.Proof = _proofBits.ToArray();
            for (int i = 0; i < _storageProofBits.Length; i++)
            {
                _accountProof.StorageProofs[i].Proof = _storageProofBits[i].ToArray();
            }

            return _accountProof;
        }

        public bool ShouldVisit(Keccak nextNode)
        {
            if (_nodeInfos.ContainsKey(nextNode))
            {
                _pathIndex = _nodeInfos[nextNode].PathIndex;
            }

            return _visitingFilter.Contains(nextNode);
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofBits(node, trieVisitContext);
            _visitingFilter.Remove(node.Keccak);

            if (trieVisitContext.IsStorage)
            {
                //                Console.WriteLine($"Visiting BRANCH {node.Keccak} at {_pathIndex}");
                foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                {
                    Keccak childHash = node.GetChildHash((byte) _storagePrefixes[storageIndex][_pathIndex]);
                    if (childHash == null)
                    {
                        Console.WriteLine($"Empty at {storageIndex}");

                        AddEmpty(node, trieVisitContext);
                    }
                    else
                    {
                        if (!_nodeInfos.ContainsKey(childHash))
                        {
                            _nodeInfos[childHash] = new NodeInfo();
                        }

                        _nodeInfos[childHash].PathIndex = _pathIndex + 1;
                        _nodeInfos[childHash].StorageIndices.Add(storageIndex);
                        //                        Console.WriteLine($"For BRANCH {storageIndex} will visit {childHash} at {_pathIndex + 1}");

                        _visitingFilter.Add(childHash);
                    }
                }
            }
            else
            {
                _visitingFilter.Add(node.GetChildHash((byte) _prefix[_pathIndex]));
            }

            _pathIndex++;
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofBits(node, trieVisitContext);
            _visitingFilter.Remove(node.Keccak);

            Keccak childHash = node.GetChildHash(0);
            if (trieVisitContext.IsStorage)
            {
                _nodeInfos[childHash] = new NodeInfo();
                _nodeInfos[childHash].PathIndex = _pathIndex + node.Path.Length;
                
                foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                {
                    bool isMatched = true;
                    for (int i = _pathIndex; i < node.Path.Length + _pathIndex; i++)
                    {
                        if ((byte) _storagePrefixes[storageIndex][i] != node.Path[i - _pathIndex])
                        {
                            isMatched = false;
                            break;
                        }
                    }

                    if (isMatched)
                    {
                        _nodeInfos[childHash].StorageIndices.Add(storageIndex);
                    }
                }
            }

            _visitingFilter.Add(childHash); // always accept so can optimize

            _pathIndex += node.Path.Length;
        }

        private void AddProofBits(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                if (_nodeInfos.ContainsKey(node.Keccak))
                {
                    foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                    {
                        _storageProofBits[storageIndex].Add(node.FullRlp.Bytes);
                    }
                }
            }
            else
            {
                _proofBits.Add(node.FullRlp.Bytes);
            }
        }

        private void AddEmpty(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                if (_nodeInfos.ContainsKey(node.Keccak))
                {
                    foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                    {
                        _storageProofBits[storageIndex].Add(Bytes.Empty);
                    }
                }
            }
            else
            {
                _proofBits.Add(Bytes.Empty);
            }
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value)
        {
            AddProofBits(node, trieVisitContext);
            _visitingFilter.Remove(node.Keccak);

            if (trieVisitContext.IsStorage)
            {
                foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                {
                    bool isMatched = true;
                    for (int i = _pathIndex; i < 64; i++)
                    {
                        if ((byte) _storagePrefixes[storageIndex][i] != node.Path[i - _pathIndex])
                        {
                            isMatched = false;
                            break;
                        }
                    }

                    if (isMatched)
                    {
                        _accountProof.StorageProofs[storageIndex].Value = new RlpStream(node.Value).DecodeByteArray();
                    }
                }
            }
            else
            {
                Account account = _accountDecoder.Decode(new RlpStream(node.Value));
                _accountProof.Nonce = account.Nonce;
                _accountProof.Balance = account.Balance;
                _accountProof.StorageRoot = account.StorageRoot;
                _accountProof.CodeHash = account.CodeHash;

                if (_storagePrefixes.Length > 0)
                {
                    _visitingFilter.Add(_accountProof.StorageRoot);
                    _nodeInfos[_accountProof.StorageRoot] = new NodeInfo();
                    _nodeInfos[_accountProof.StorageRoot].PathIndex = 0;
                    for (int i = 0; i < _storagePrefixes.Length; i++)
                    {
                        _nodeInfos[_accountProof.StorageRoot].StorageIndices.Add(i);
                    }
                }
            }

            _pathIndex = 0;
        }

        private AccountDecoder _accountDecoder = new AccountDecoder();

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            throw new InvalidOperationException($"{nameof(AccountProofCollector)} does never expect to visit code");
        }
    }
}