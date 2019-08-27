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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Store;

namespace Nethermind.JsonRpc.Eip1186
{
    public class ProofCollector : ITreeVisitor
    {
        private int _pathIndex;

        private readonly Address _address;
        private readonly Keccak[] _storageKeys;

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

        public ProofCollector(Address address, params byte[][] storageKeys)
            : this(address, storageKeys.Select(ToKey).ToArray())
        {
        }

        public ProofCollector(Address address, Keccak[] storageKeys)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _storageKeys = storageKeys ?? new Keccak[0];

            _accountProof = new AccountProof();
            _accountProof.StorageProofs = new StorageProof[_storageKeys.Length];

            _storagePrefixes = new Nibble[_storageKeys.Length][];
            _storageProofBits = new List<byte[]>[_storageKeys.Length];
            for (int i = 0; i < _storageProofBits.Length; i++)
            {
                _storageProofBits[i] = new List<byte[]>();
            }

            for (int i = 0; i < _storageKeys.Length; i++)
            {
                _storagePrefixes[i] = Nibbles.FromBytes(_storageKeys[i].Bytes);

                _accountProof.StorageProofs[i] = new StorageProof();
                _accountProof.StorageProofs[i].Key = _storageKeys[i];
            }
        }

        private AccountProof _accountProof;

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

        public void VisitTree(Keccak rootHash, VisitContext visitContext)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, VisitContext visitContext)
        {
        }

        public void VisitBranch(TrieNode node, VisitContext visitContext)
        {
            AddProofBits(node, visitContext);
            _visitingFilter.Remove(node.Keccak);

            if (visitContext.IsStorage)
            {
//                Console.WriteLine($"Visiting BRANCH {node.Keccak} at {_pathIndex}");
                foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                {
                    Keccak childHash = node.GetChildHash((byte) _storagePrefixes[storageIndex][_pathIndex]);
                    if (childHash == null)
                    {
                        Console.WriteLine($"Empty at {storageIndex}");
                        
                        AddEmpty(node, visitContext);
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

        public void VisitExtension(TrieNode node, VisitContext visitContext)
        {
            AddProofBits(node, visitContext);
            _visitingFilter.Remove(node.Keccak);

            Keccak childHash = node.GetChildHash(0);
            if (visitContext.IsStorage)
            {
//                Console.WriteLine($"Visiting EXT {node.Keccak} at {_pathIndex}");
//                Console.WriteLine($"Node {node.Keccak} has storage indices {string.Join(';', _nodeInfos[node.Keccak].StorageIndices)} at {_pathIndex + node.Path.Length}");
                _nodeInfos[childHash] = new NodeInfo();
                _nodeInfos[childHash].PathIndex = _pathIndex + node.Path.Length;
                _nodeInfos[childHash].StorageIndices.AddRange(_nodeInfos[node.Keccak].StorageIndices);

//                Console.WriteLine($"For EXT {string.Join(';', _nodeInfos[node.Keccak].StorageIndices)} will visit {childHash} at {_pathIndex + node.Path.Length}");
            }

            _visitingFilter.Add(childHash); // always accept so can optimize

            _pathIndex += node.Path.Length;
        }

        private void AddProofBits(TrieNode node, VisitContext visitContext)
        {
            if (visitContext.IsStorage)
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

        private void AddEmpty(TrieNode node, VisitContext visitContext)
        {
            if (visitContext.IsStorage)
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

        public void VisitLeaf(TrieNode node, VisitContext visitContext, byte[] value)
        {
            AddProofBits(node, visitContext);
            _visitingFilter.Remove(node.Keccak);

            if (visitContext.IsStorage)
            {
//                Console.WriteLine($"Visiting LEAF {node.Keccak} at {_pathIndex} - node value is {node.Value.ToHexString()}");
                foreach (int storageIndex in _nodeInfos[node.Keccak].StorageIndices)
                {
//                    Console.WriteLine($"Setting LEAF value for {storageIndex} {node.Keccak} at {_pathIndex} - node value is {node.Value.ToHexString()}");
                    _accountProof.StorageProofs[storageIndex].Value = new RlpStream(node.Value).DecodeByteArray();
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

        public void VisitCode(Keccak codeHash, byte[] code, VisitContext visitContext)
        {
            throw new InvalidOperationException($"{nameof(ProofCollector)} does never expect to visit code");
        }
    }
}