// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style proof collector.
    /// Uses path-based traversal via <see cref="TreePathContextWithStorage"/> instead of
    /// hash-based node tracking, which correctly handles inline trie nodes (nodes smaller
    /// than 32 bytes that have no standalone hash).
    /// </summary>
    public class AccountProofCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        private readonly Address _address = Address.Zero;
        private readonly AccountProof _accountProof;

        private readonly Nibble[] _fullAccountPath;
        private readonly Nibble[][] _fullStoragePaths;

        private readonly List<byte[]> _accountProofItems = new();
        private readonly List<byte[]>[] _storageProofItems;

        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        private static ValueHash256 ToKey(byte[] index) => ValueKeccak.Compute(index);

        private static byte[] ToKey(UInt256 index)
        {
            byte[] bytes = new byte[32];
            index.ToBigEndian(bytes);
            return bytes;
        }

        private AccountProofCollector(ReadOnlySpan<byte> hashedAddress, IEnumerable<ValueHash256>? keccakStorageKeys, int length, byte[][]? storageKeys)
        {
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

            int j = 0;
            foreach (ValueHash256 storageKey in keccakStorageKeys)
            {
                _fullStoragePaths[j] = Nibbles.FromBytes(storageKey.Bytes);
                _accountProof.StorageProofs[j] = new StorageProof
                {
                    Key = storageKeys?[j].ToHexString(true, true),
                    Value = Bytes.ZeroByte
                };
                j++;
            }
        }

        /// <summary>Only for testing</summary>
        internal AccountProofCollector(ReadOnlySpan<byte> hashedAddress, Hash256[]? keccakStorageKeys)
            : this(hashedAddress, keccakStorageKeys?.Select(static keccak => (ValueHash256)keccak).ToArray()) { }

        /// <summary>Only for testing</summary>
        internal AccountProofCollector(ReadOnlySpan<byte> hashedAddress, ValueHash256[]? keccakStorageKeys)
            : this(hashedAddress, keccakStorageKeys, keccakStorageKeys?.Length ?? 0, null) { }

        public AccountProofCollector(ReadOnlySpan<byte> hashedAddress, params byte[][]? storageKeys)
            : this(hashedAddress, storageKeys?.Select(ToKey), storageKeys?.Length ?? 0, storageKeys) { }

        public AccountProofCollector(Address? address, params byte[][] storageKeys)
            : this(Keccak.Compute((address ?? Address.Zero).Bytes).Bytes, storageKeys)
            => _accountProof.Address = _address = address ?? throw new ArgumentNullException(nameof(address));

        public AccountProofCollector(Address? address, IEnumerable<UInt256> storageKeys)
            : this(address, storageKeys.Select(ToKey).ToArray()) { }

        public AccountProof BuildResult()
        {
            _accountProof.Proof = _accountProofItems.ToArray();
            for (int i = 0; i < _storageProofItems.Length; i++)
            {
                _accountProof.StorageProofs![i].Proof = _storageProofItems[i].ToArray();
            }
            return _accountProof;
        }

        public (IReadOnlyList<byte[]> AccountProof, IReadOnlyList<byte[]>[] StorageProof) GetRawResult()
            => (_accountProofItems, _storageProofItems);

        public bool IsFullDbScan => false;

        public bool ShouldVisit(in TreePathContextWithStorage ctx, in ValueHash256 nextNode)
        {
            if (ctx.Storage is null)
            {
                // Account trie: follow the path leading to our target account
                return IsPrefix(_fullAccountPath, ctx.Path);
            }

            // Storage trie: visit nodes on the path to any requested storage slot
            for (int i = 0; i < _fullStoragePaths.Length; i++)
            {
                if (IsPrefix(_fullStoragePaths[i], ctx.Path))
                    return true;
            }
            return false;
        }

        public void VisitTree(in TreePathContextWithStorage ctx, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in TreePathContextWithStorage ctx, in ValueHash256 nodeHash) { }

        public void VisitBranch(in TreePathContextWithStorage ctx, TrieNode node)
            => AddProofItem(node, ctx);

        public void VisitExtension(in TreePathContextWithStorage ctx, TrieNode node)
            => AddProofItem(node, ctx);

        public void VisitLeaf(in TreePathContextWithStorage ctx, TrieNode node)
        {
            AddProofItem(node, ctx);

            if (ctx.Storage is null)
            {
                // Decode account fields only if this leaf is our target account
                if (IsFullPathMatch(_fullAccountPath, ctx.Path, node.Key))
                {
                    Rlp.ValueDecoderContext rlpCtx = new(node.Value.ToArray());
                    Account account = _accountDecoder.Decode(ref rlpCtx);
                    _accountProof.Nonce = account.Nonce;
                    _accountProof.Balance = account.Balance;
                    _accountProof.StorageRoot = account.StorageRoot;
                    _accountProof.CodeHash = account.CodeHash;
                }
            }
            else
            {
                // Record decoded storage values for every slot whose full path matches this leaf
                for (int i = 0; i < _fullStoragePaths.Length; i++)
                {
                    if (IsFullPathMatch(_fullStoragePaths[i], ctx.Path, node.Key))
                    {
                        _accountProof.StorageProofs[i].Value = DecodeStorageValue(node.Value);
                    }
                }
            }
        }

        public void VisitAccount(in TreePathContextWithStorage ctx, TrieNode node, in AccountStruct account) { }

        private void AddProofItem(TrieNode node, in TreePathContextWithStorage ctx)
        {
            if (ctx.Storage is null)
            {
                _accountProofItems.Add(node.FullRlp.ToArray());
            }
            else
            {
                // Add the node RLP to every storage proof whose path passes through this node
                for (int i = 0; i < _fullStoragePaths.Length; i++)
                {
                    if (IsPrefix(_fullStoragePaths[i], ctx.Path))
                        _storageProofItems[i].Add(node.FullRlp.ToArray());
                }
            }
        }


        /// <summary>
        /// Decodes a storage leaf value. Handles both RLP-encoded values (standard) and raw bytes.
        /// </summary>
        private static ReadOnlyMemory<byte> DecodeStorageValue(ReadOnlySpan<byte> nodeValue)
        {
            if (nodeValue.IsEmpty) return new byte[] { 0 };
            try
            {
                return new Rlp.ValueDecoderContext(nodeValue).DecodeByteArray();
            }
            catch (RlpException)
            {
                // Value was stored without RLP encoding; return as-is
                return nodeValue.ToArray();
            }
        }

        /// <summary>
        /// Returns true if <paramref name="currentPath"/> is a prefix of <paramref name="targetPath"/>.
        /// An empty path is a prefix of every target.
        /// </summary>
        private static bool IsPrefix(Nibble[] targetPath, TreePath currentPath)
        {
            if (currentPath.Length > targetPath.Length) return false;
            for (int i = 0; i < currentPath.Length; i++)
            {
                if (currentPath[i] != (byte)targetPath[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if the full trie path (context path + leaf node key) exactly equals <paramref name="targetPath"/>.
        /// </summary>
        private static bool IsFullPathMatch(Nibble[] targetPath, TreePath ctxPath, byte[]? nodeKey)
        {
            if (nodeKey is null || ctxPath.Length + nodeKey.Length != targetPath.Length) return false;

            for (int i = 0; i < ctxPath.Length; i++)
            {
                if (ctxPath[i] != (byte)targetPath[i]) return false;
            }

            for (int i = 0; i < nodeKey.Length; i++)
            {
                if (nodeKey[i] != (byte)targetPath[ctxPath.Length + i]) return false;
            }

            return true;
        }
    }
}
