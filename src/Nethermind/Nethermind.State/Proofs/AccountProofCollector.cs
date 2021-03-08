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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style proof collector
    /// </summary>
    public class AccountProofCollector : ITreeVisitor
    {
        private int _pathTraversalIndex;
        private Address _address = Address.Zero;
        private AccountProof _accountProof;

        private Nibble[] _fullAccountPath;
        private Nibble[][] _fullStoragePaths;

        private List<byte[]> _accountProofItems = new();
        private List<byte[]>[] _storageProofItems;

        private Dictionary<Keccak, StorageNodeInfo> _storageNodeInfos = new();
        private HashSet<Keccak> _nodeToVisitFilter = new();

        private class StorageNodeInfo
        {
            public StorageNodeInfo()
            {
                StorageIndices = new List<int>();
            }

            public int PathIndex { get; set; }
            public List<int> StorageIndices { get; }
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

        internal AccountProofCollector(byte[] hashedAddress, params byte[][] storageKeys)
        {
            storageKeys ??= Array.Empty<byte[]>();
            _fullAccountPath = Nibbles.FromBytes(hashedAddress);

            Keccak[] localStorageKeys = storageKeys.Select(ToKey).ToArray();

            _accountProof = new AccountProof();
            _accountProof.StorageProofs = new StorageProof[localStorageKeys.Length];
            _accountProof.Address = _address;

            _fullStoragePaths = new Nibble[localStorageKeys.Length][];
            _storageProofItems = new List<byte[]>[localStorageKeys.Length];
            for (int i = 0; i < _storageProofItems.Length; i++)
            {
                _storageProofItems[i] = new List<byte[]>();
            }

            for (int i = 0; i < localStorageKeys.Length; i++)
            {
                _fullStoragePaths[i] = Nibbles.FromBytes(localStorageKeys[i].Bytes);

                _accountProof.StorageProofs[i] = new StorageProof();
                _accountProof.StorageProofs[i].Key = storageKeys[i];
                _accountProof.StorageProofs[i].Value = new byte[] {0};
            }
        }

        public AccountProofCollector(Address address, params byte[][] storageKeys)
            : this(Keccak.Compute(address?.Bytes ?? Address.Zero.Bytes).Bytes, storageKeys)
        {
            _accountProof.Address = _address = address ?? throw new ArgumentNullException(nameof(address));
        }
        
        public AccountProofCollector(Address address, UInt256[] storageKeys)
            : this(address, storageKeys.Select(ToKey).ToArray())
        {
            _accountProof.Address = _address = address ?? throw new ArgumentNullException(nameof(address));
        }

        public AccountProof BuildResult()
        {
            _accountProof.Proof = _accountProofItems.ToArray();
            for (int i = 0; i < _storageProofItems.Length; i++)
            {
                _accountProof.StorageProofs[i].Proof = _storageProofItems[i].ToArray();
            }

            return _accountProof;
        }

        public bool ShouldVisit(Keccak nextNode)
        {
            if (_storageNodeInfos.ContainsKey(nextNode))
            {
                _pathTraversalIndex = _storageNodeInfos[nextNode].PathIndex;
            }

            return _nodeToVisitFilter.Contains(nextNode);
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofItem(node, trieVisitContext);
            _nodeToVisitFilter.Remove(node.Keccak);

            if (trieVisitContext.IsStorage)
            {
                HashSet<int> bumpedIndexes = new();
                foreach (int storageIndex in _storageNodeInfos[node.Keccak].StorageIndices)
                {
                    Nibble childIndex = _fullStoragePaths[storageIndex][_pathTraversalIndex];
                    Keccak childHash = node.GetChildHash((byte) childIndex);
                    if (childHash is null)
                    {
                        AddEmpty(node, trieVisitContext);
                    }
                    else
                    {
                        if (!_storageNodeInfos.ContainsKey(childHash))
                        {
                            _storageNodeInfos[childHash] = new StorageNodeInfo();
                        }

                        if (!bumpedIndexes.Contains((byte) childIndex))
                        {
                            bumpedIndexes.Add((byte) childIndex);
                            _storageNodeInfos[childHash].PathIndex = _pathTraversalIndex + 1;
                        }
                        
                        _storageNodeInfos[childHash].StorageIndices.Add(storageIndex);
                        _nodeToVisitFilter.Add(childHash);
                    }
                }
            }
            else
            {
                _nodeToVisitFilter.Add(node.GetChildHash((byte) _fullAccountPath[_pathTraversalIndex]));
            }

            _pathTraversalIndex++;
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofItem(node, trieVisitContext);
            _nodeToVisitFilter.Remove(node.Keccak);

            Keccak childHash = node.GetChildHash(0);
            if (trieVisitContext.IsStorage)
            {
                _storageNodeInfos[childHash] = new StorageNodeInfo();
                _storageNodeInfos[childHash].PathIndex = _pathTraversalIndex + node.Path.Length;

                foreach (int storageIndex in _storageNodeInfos[node.Keccak].StorageIndices)
                {
                    bool isPathMatched = IsPathMatched(node, _fullStoragePaths[storageIndex]);
                    if (isPathMatched)
                    {
                        _storageNodeInfos[childHash].StorageIndices.Add(storageIndex);
                        _nodeToVisitFilter.Add(childHash); // always accept so can optimize
                    }
                }
            }

            if (IsPathMatched(node, _fullAccountPath))
            {
                _nodeToVisitFilter.Add(childHash); // always accept so can optimize
                _pathTraversalIndex += node.Path.Length;
            }
        }

        private void AddProofItem(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                if (_storageNodeInfos.ContainsKey(node.Keccak))
                {
                    foreach (int storageIndex in _storageNodeInfos[node.Keccak].StorageIndices)
                    {
                        _storageProofItems[storageIndex].Add(node.FullRlp);
                    }
                }
            }
            else
            {
                _accountProofItems.Add(node.FullRlp);
            }
        }

        private void AddEmpty(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                if (_storageNodeInfos.ContainsKey(node.Keccak))
                {
                    foreach (int storageIndex in _storageNodeInfos[node.Keccak].StorageIndices)
                    {
                        _storageProofItems[storageIndex].Add(Array.Empty<byte>());
                    }
                }
            }
            else
            {
                _accountProofItems.Add(Array.Empty<byte>());
            }
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value)
        {
            AddProofItem(node, trieVisitContext);
            _nodeToVisitFilter.Remove(node.Keccak);

            if (trieVisitContext.IsStorage)
            {
                foreach (int storageIndex in _storageNodeInfos[node.Keccak].StorageIndices)
                {
                    Nibble[] thisStoragePath = _fullStoragePaths[storageIndex];
                    bool isPathMatched = IsPathMatched(node, thisStoragePath);
                    if (isPathMatched)
                    {
                        _accountProof.StorageProofs[storageIndex].Value = new RlpStream(node.Value).DecodeByteArray();
                    }
                }
            }
            else
            {
                Account account = _accountDecoder.Decode(new RlpStream(node.Value));
                bool isPathMatched = IsPathMatched(node, _fullAccountPath);
                if (isPathMatched)
                {
                    _accountProof.Nonce = account.Nonce;
                    _accountProof.Balance = account.Balance;
                    _accountProof.StorageRoot = account.StorageRoot;
                    _accountProof.CodeHash = account.CodeHash;

                    if (_fullStoragePaths.Length > 0)
                    {
                        _nodeToVisitFilter.Add(_accountProof.StorageRoot);
                        _storageNodeInfos[_accountProof.StorageRoot] = new StorageNodeInfo();
                        _storageNodeInfos[_accountProof.StorageRoot].PathIndex = 0;
                        for (int i = 0; i < _fullStoragePaths.Length; i++)
                        {
                            _storageNodeInfos[_accountProof.StorageRoot].StorageIndices.Add(i);
                        }
                    }
                }
            }

            _pathTraversalIndex = 0;
        }

        private bool IsPathMatched(TrieNode node, Nibble[] path)
        {
            bool isPathMatched = true;
            for (int i = _pathTraversalIndex; i < node.Path.Length + _pathTraversalIndex; i++)
            {
                if ((byte) path[i] != node.Path[i - _pathTraversalIndex])
                {
                    isPathMatched = false;
                    break;
                }
            }

            return isPathMatched;
        }

        private AccountDecoder _accountDecoder = new();

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            throw new InvalidOperationException($"{nameof(AccountProofCollector)} does never expect to visit code");
        }
    }
}
