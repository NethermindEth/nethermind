// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style proof collector
    /// </summary>
    public class AccountProofCollector : ITreeVisitor<OldStyleTrieVisitContext>
    {
        private int _pathTraversalIndex;
        private readonly Address _address = Address.Zero;
        private readonly AccountProof _accountProof;

        private readonly Nibble[] _fullAccountPath;
        private readonly Nibble[][] _fullStoragePaths;

        private readonly List<byte[]> _accountProofItems = new();
        private readonly List<byte[]>[] _storageProofItems;

        private readonly Dictionary<Hash256AsKey, StorageNodeInfo> _storageNodeInfos = new(Hash256AsKeyComparer.Instance);
        private readonly Dictionary<Hash256AsKey, StorageNodeInfo>.AlternateLookup<ValueHash256> _storageNodeInfosLookup;
        private readonly HashSet<Hash256AsKey> _nodeToVisitFilter = new(Hash256AsKeyComparer.Instance);
        private readonly HashSet<Hash256AsKey>.AlternateLookup<ValueHash256> _nodeToVisitFilterLookup;
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        private class StorageNodeInfo
        {
            public int PathIndex { get; set; }
            public List<int> StorageIndices { get; } = new();
        }

        private static ValueHash256 ToKey(byte[] index) => ValueKeccak.Compute(index);

        private static byte[] ToKey(UInt256 index)
        {
            byte[] bytes = new byte[32];
            index.ToBigEndian(bytes);
            return bytes;
        }

        private AccountProofCollector(ReadOnlySpan<byte> hashedAddress, IEnumerable<ValueHash256>? keccakStorageKeys, int length, byte[][]? storageKeys)
        {
            _storageNodeInfosLookup = _storageNodeInfos.GetAlternateLookup<ValueHash256>();
            _nodeToVisitFilterLookup = _nodeToVisitFilter.GetAlternateLookup<ValueHash256>();

            keccakStorageKeys ??= [];

            _fullAccountPath = Nibbles.FromBytes(hashedAddress);

            _accountProof = new AccountProof
            {
                StorageProofs = new StorageProof[length],
                Address = _address
            };

            _fullStoragePaths = new Nibble[length][];
            _storageProofItems = new List<byte[]>[length];
            for (int i = 0; i < _storageProofItems.Length; i++)
            {
                _storageProofItems[i] = new List<byte[]>();
            }

            if (keccakStorageKeys is not null)
            {
                int i = 0;
                foreach (ValueHash256 storageKey in keccakStorageKeys)
                {
                    _fullStoragePaths[i] = Nibbles.FromBytes(storageKey.Bytes);

                    _accountProof.StorageProofs[i] = new StorageProof
                    {
                        // we don't know the key (index)
                        Key = storageKeys?[i],
                        Value = Bytes.ZeroByte
                    };

                    i++;
                }
            }
        }

        /// <summary>
        /// Only for testing
        /// </summary>
        internal AccountProofCollector(ReadOnlySpan<byte> hashedAddress, Hash256[]? keccakStorageKeys)
            : this(hashedAddress, keccakStorageKeys?.Select(static keccak => (ValueHash256)keccak).ToArray())
        {
        }

        /// <summary>
        /// Only for testing too
        /// </summary>
        internal AccountProofCollector(ReadOnlySpan<byte> hashedAddress, ValueHash256[]? keccakStorageKeys)
            : this(hashedAddress, keccakStorageKeys, keccakStorageKeys?.Length ?? 0, null)
        {
        }

        public AccountProofCollector(ReadOnlySpan<byte> hashedAddress, params byte[][]? storageKeys)
            : this(hashedAddress, storageKeys?.Select(ToKey), storageKeys?.Length ?? 0, storageKeys)
        {
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

        public bool ShouldVisit(in OldStyleTrieVisitContext _, in ValueHash256 nextNode)
        {
            if (_storageNodeInfosLookup.TryGetValue(nextNode, out StorageNodeInfo value))
            {
                _pathTraversalIndex = value.PathIndex;
            }

            return _nodeToVisitFilterLookup.Contains(nextNode);
        }

        public void VisitTree(in OldStyleTrieVisitContext _, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in OldStyleTrieVisitContext _, in ValueHash256 nodeHash)
        {
        }

        public void VisitBranch(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
        {
            AddProofItem(node, trieVisitContext);
            _nodeToVisitFilter.Remove(node.Keccak);

            if (trieVisitContext.IsStorage)
            {
                HashSet<int> bumpedIndexes = new();
                foreach (int storageIndex in _storageNodeInfos[node.Keccak].StorageIndices)
                {
                    Nibble childIndex = _fullStoragePaths[storageIndex][_pathTraversalIndex];
                    byte[] child = node.GetChildHashOrRlp((byte)childIndex);

                    if (child?.Length == 32)
                    {
                        // If the length is 32 it's the hash of the child
                        Hash256 childHash = new(child);
                        ref StorageNodeInfo? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageNodeInfos, childHash, out bool exists);
                        if (!exists)
                        {
                            value = new StorageNodeInfo();
                        }

                        if (!bumpedIndexes.Contains((byte)childIndex))
                        {
                            bumpedIndexes.Add((byte)childIndex);
                            value.PathIndex = _pathTraversalIndex + 1;
                        }

                        value.StorageIndices.Add(storageIndex);
                        _nodeToVisitFilter.Add(childHash);
                    }
                    else if (child is not null)
                    {
                        // Child is an inline node, decode RLP and get storage value
                        var rlpStream = new RlpStream(child);
                        _ = rlpStream.ReadSequenceLength();
                        _ = rlpStream.DecodeByteArraySpan();
                        ReadOnlySpan<byte> value = rlpStream.DecodeByteArraySpan();
                        _accountProof.StorageProofs[storageIndex].Value = value.ToArray();
                    }
                }
            }
            else
            {
                _nodeToVisitFilter.Add(node.GetChildHash((byte)_fullAccountPath[_pathTraversalIndex]));
            }

            _pathTraversalIndex++;
        }

        public void VisitExtension(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
        {
            AddProofItem(node, trieVisitContext);
            _nodeToVisitFilter.Remove(node.Keccak);

            Hash256 childHash = node.GetChildHash(0);
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

        private void AddProofItem(TrieNode node, OldStyleTrieVisitContext trieVisitContext)
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

        public void VisitLeaf(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
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

        public void VisitAccount(in OldStyleTrieVisitContext _, TrieNode node, in AccountStruct account)
        {
        }
    }
}
