// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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

        /// <summary>
        /// Only for testing
        /// </summary>
        public AccountProofCollector(ReadOnlySpan<byte> hashedAddress, Keccak[] keccakStorageKeys)
            : this(hashedAddress, keccakStorageKeys.Select((keccak) => (ValueKeccak)keccak).ToArray())
        {
        }

        /// <summary>
        /// Only for testing too
        /// </summary>
        public AccountProofCollector(ReadOnlySpan<byte> hashedAddress, ValueKeccak[] keccakStorageKeys)
        {
            keccakStorageKeys ??= Array.Empty<ValueKeccak>();

            _fullAccountPath = Nibbles.FromBytes(hashedAddress);

            _accountProof = new AccountProof();
            _accountProof.StorageProofs = new StorageProof[keccakStorageKeys.Length];
            _accountProof.Address = _address;

            _fullStoragePaths = new Nibble[keccakStorageKeys.Length][];
            _storageProofItems = new List<byte[]>[keccakStorageKeys.Length];
            for (int i = 0; i < _storageProofItems.Length; i++)
            {
                _storageProofItems[i] = new List<byte[]>();
            }

            for (int i = 0; i < keccakStorageKeys.Length; i++)
            {
                _fullStoragePaths[i] = Nibbles.FromBytes(keccakStorageKeys[i].Bytes);

                _accountProof.StorageProofs[i] = new StorageProof();
                // we don't know the key (index)
                //_accountProof.StorageProofs[i].Key = storageKeys[i];
                _accountProof.StorageProofs[i].Value = new byte[] { 0 };
            }
        }

        public AccountProofCollector(ReadOnlySpan<byte> hashedAddress, params byte[][] storageKeys)
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
                _accountProof.StorageProofs[i].Value = new byte[] { 0 };
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

        public bool IsFullDbScan => false;

        public bool ShouldVisit(Keccak nextNode)
        {
            if (_storageNodeInfos.TryGetValue(nextNode, out StorageNodeInfo value))
            {
                _pathTraversalIndex = value.PathIndex;
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
                    Keccak childHash = node.GetChildHash((byte)childIndex);
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

                        if (!bumpedIndexes.Contains((byte)childIndex))
                        {
                            bumpedIndexes.Add((byte)childIndex);
                            _storageNodeInfos[childHash].PathIndex = _pathTraversalIndex + 1;
                        }

                        _storageNodeInfos[childHash].StorageIndices.Add(storageIndex);
                        _nodeToVisitFilter.Add(childHash);
                    }
                }
            }
            else
            {
                _nodeToVisitFilter.Add(node.GetChildHash((byte)_fullAccountPath[_pathTraversalIndex]));
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
                _storageNodeInfos[childHash].PathIndex = _pathTraversalIndex + node.Key.Length;

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
                _pathTraversalIndex += node.Key.Length;
            }
        }

        private void AddProofItem(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                if (_storageNodeInfos.TryGetValue(node.Keccak, out StorageNodeInfo value))
                {
                    foreach (int storageIndex in value.StorageIndices)
                    {
                        _storageProofItems[storageIndex].Add(node.FullRlp.ToArray());
                    }
                }
            }
            else
            {
                _accountProofItems.Add(node.FullRlp.ToArray());
            }
        }

        private void AddEmpty(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                if (_storageNodeInfos.TryGetValue(node.Keccak, out StorageNodeInfo value))
                {
                    foreach (int storageIndex in value.StorageIndices)
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
                        _accountProof.StorageProofs[storageIndex].Value = new RlpStream(node.Value.ToArray()).DecodeByteArray();
                    }
                }
            }
            else
            {
                Account account = _accountDecoder.Decode(new RlpStream(node.Value.ToArray()));
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
            for (int i = _pathTraversalIndex; i < node.Key.Length + _pathTraversalIndex; i++)
            {
                if ((byte)path[i] != node.Key[i - _pathTraversalIndex])
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
