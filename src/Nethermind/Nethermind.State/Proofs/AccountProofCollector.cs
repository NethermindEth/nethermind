// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nethermind.State.Flat.History")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nethermind.State.Flat.History.Test")]

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
        private bool _accountExists;

        private readonly Nibble[] _fullAccountPath;
        private readonly Nibble[][] _fullStoragePaths;

        private readonly List<byte[]> _accountProofItems = [];
        private readonly List<byte[]>[] _storageProofItems;
        private readonly CancellationToken _cancellationToken;

        internal CancellationToken CancellationToken => _cancellationToken;

        internal ValueHash256 HashedAddress => Pack(_fullAccountPath);

        internal ValueHash256[] GetHashedStorageKeys()
        {
            ValueHash256[] keys = new ValueHash256[_fullStoragePaths.Length];
            for (int i = 0; i < keys.Length; i++) keys[i] = Pack(_fullStoragePaths[i]);
            return keys;
        }

        private static ValueHash256 Pack(Nibble[] nibbles)
        {
            Span<byte> bytes = stackalloc byte[Hash256.Size];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(((byte)nibbles[2 * i] << 4) | (byte)nibbles[2 * i + 1]);
            return new ValueHash256(bytes);
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
                _storageProofItems[i] = [];
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

        public AccountProofCollector(Address address, params byte[][] storageKeys)
            : this(Keccak.Compute(address.Bytes).Bytes, storageKeys)
            => _accountProof.Address = _address = address;

        public AccountProofCollector(Address address, IEnumerable<UInt256> storageKeys)
            : this(address, storageKeys.Select(ToKey).ToArray()) { }

        public AccountProofCollector(Address address, IReadOnlyCollection<UInt256> storageKeys, CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
            _accountProof = new AccountProof
            {
                StorageProofs = new StorageProof[storageKeys.Count],
                Address = _address = address
            };
            _fullAccountPath = Nibbles.FromBytes(ValueKeccak.Compute(_address.Bytes).Bytes);
            _fullStoragePaths = new Nibble[storageKeys.Count][];
            _storageProofItems = new List<byte[]>[storageKeys.Count];

            if (Avx2.IsSupported && storageKeys.Count >= (Avx512F.IsSupported ? 2 : Avx2HashBatchSize))
            {
                InitializeBatchedStorageProofs(storageKeys);
                return;
            }

            ValueHash256 keyBuffer = default;
            using IEnumerator<UInt256> keys = storageKeys.GetEnumerator();
            for (int j = 0; keys.MoveNext(); j++)
            {
                keys.Current.ToBigEndian(keyBuffer.BytesAsSpan);
                _fullStoragePaths[j] = Nibbles.FromBytes(ValueKeccak.Compute(keyBuffer.Bytes).Bytes);
                SetStorageProof(j, keyBuffer.Bytes);
            }
        }

        private void SetStorageProof(int index, ReadOnlySpan<byte> key)
        {
            _storageProofItems[index] = [];
            _accountProof.StorageProofs[index] = new StorageProof
            {
                Key = key.ToHexString(true, true),
                Value = Bytes.ZeroByte
            };
        }

        private const int Avx2HashBatchSize = 4;
        private const int MaxHashBatchSize = 8;
        private const int KeccakRate = 136;
        private const int VectorByteLength = 32;
        private const int StorageHashBufferLength = MaxHashBatchSize * (KeccakRate + Keccak.Size);

        [InlineArray(StorageHashBufferLength / VectorByteLength)]
        private struct StorageHashBuffer
        {
            private Vector256<byte> _element0;
        }

        [SkipLocalsInit]
        private void InitializeBatchedStorageProofs(IReadOnlyCollection<UInt256> storageKeys)
        {
            int rate = Avx512F.IsSupported ? Keccak.Size : KeccakRate;
            int batchSize = Avx512F.IsSupported ? MaxHashBatchSize : Avx2HashBatchSize;
            Unsafe.SkipInit(out StorageHashBuffer buffer);
            Span<byte> storage = MemoryMarshal.AsBytes((Span<Vector256<byte>>)buffer);
            Span<byte> blocks = storage[..(batchSize * rate)];
            Span<byte> hashes = storage[(MaxHashBatchSize * rate)..];
            blocks.Clear();
            for (int i = 0; !Avx512F.IsSupported && i < batchSize; i++)
            {
                blocks[i * rate + Keccak.Size] = 1;
                blocks[i * rate + rate - 1] = 128;
            }
            int j = 0;
            int pending = 0;
            using IEnumerator<UInt256> keys = storageKeys.GetEnumerator();
            for (; keys.MoveNext(); j++)
            {
                Span<byte> key = blocks.Slice(pending * rate, Keccak.Size);
                keys.Current.ToBigEndian(key);
                SetStorageProof(j, key);
                if (++pending == batchSize)
                {
                    SetStoragePaths(blocks, hashes, j + 1 - batchSize, batchSize);
                    pending = 0;
                }
            }
            int tailStart = 0;
            if (pending >= (Avx512F.IsSupported ? 2 : Avx2HashBatchSize))
            {
                SetStoragePaths(blocks, hashes, j - pending, Avx512F.IsSupported ? pending : Avx2HashBatchSize);
                tailStart = Avx512F.IsSupported ? pending : Avx2HashBatchSize;
            }
            for (int i = tailStart; i < pending; i++)
                _fullStoragePaths[j - pending + i] = Nibbles.FromBytes(ValueKeccak.Compute(blocks.Slice(i * rate, Keccak.Size)).Bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetStoragePaths(Span<byte> blocks, Span<byte> hashes, int start, int count)
        {
            if (Avx512F.IsSupported)
                KeccakHash.ComputeHash32Bytes8Avx512(ref blocks[0], ref hashes[0]);
            else
                KeccakHash.ComputePaddedBlocks4Avx2(ref blocks[0], ref hashes[0]);
            for (int i = 0; i < count; i++)
                _fullStoragePaths[start + i] = Nibbles.FromBytes(hashes.Slice(i * Keccak.Size, Keccak.Size));
        }

        public AccountProof BuildResult()
        {
            // EIP-1186 distinguishes a non-existent account from an empty existing account by
            // returning zero hashes for the absent account case instead of the canonical empty hashes.
            if (!_accountExists)
            {
                _accountProof.CodeHash = Hash256.Zero;
                _accountProof.StorageRoot = Hash256.Zero;
            }

            _accountProof.Proof = _accountProofItems.ToArray();
            for (int i = 0; i < _storageProofItems.Length; i++)
            {
                _accountProof.StorageProofs[i].Proof = _storageProofItems[i].ToArray();
            }
            return _accountProof;
        }

        public (IReadOnlyList<byte[]> AccountProof, IReadOnlyList<byte[]>[] StorageProof) GetRawResult()
            => (_accountProofItems, _storageProofItems);

        public bool IsFullDbScan => false;

        public bool ShouldVisit(in TreePathContextWithStorage ctx, in ValueHash256 nextNode)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (ctx.Storage is null)
            {
                // Account trie: follow the path leading to our target account. Once we've reached
                // the target leaf, only descend further (into the storage trie) when storage slots
                // were actually requested.
                if (!IsPrefix(_fullAccountPath, ctx.Path)) return false;
                return _fullStoragePaths.Length != 0 || ctx.Path.Length < _fullAccountPath.Length;
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

            // Account-leaf fields are written from VisitAccount (the visitor framework decodes
            // the account for us, so we avoid decoding the same RLP twice).
            if (ctx.Storage is null) return;

            // Storage leaf: record the decoded value for every requested slot whose full path
            // ends at this leaf. Storage leaf Values are always RLP-encoded in a valid trie.
            for (int i = 0; i < _fullStoragePaths.Length; i++)
            {
                if (IsFullPathMatch(_fullStoragePaths[i], ctx.Path, node.Key))
                    _accountProof.StorageProofs[i].Value = new RlpReader(node.Value.AsSpan()).DecodeByteArray();
            }
        }

        public void VisitAccount(in TreePathContextWithStorage ctx, TrieNode node, in AccountStruct account)
        {
            // ctx.Path here already includes the leaf's key (it's leafContext, not nodeContext).
            if (!IsFullPathMatch(_fullAccountPath, ctx.Path)) return;

            _accountExists = true;
            _accountProof.Nonce = account.Nonce;
            _accountProof.Balance = account.Balance;
            _accountProof.StorageRoot = account.StorageRoot.ToCommitment();
            _accountProof.CodeHash = account.CodeHash.ToCommitment();
        }

        private void AddProofItem(TrieNode node, in TreePathContextWithStorage ctx)
        {
            // Inline nodes have no standalone hash; their RLP is already embedded in the parent's
            // RLP, so EIP-1186 / go-ethereum convention is to omit them from the proof entries.
            if (node.Keccak is null) return;

            byte[] rlp = node.FullRlp.ToArray() ?? throw new TrieException("A visited proof node must have encoded RLP.");
            if (ctx.Storage is null)
            {
                _accountProofItems.Add(rlp);
                return;
            }

            // Add the node RLP to every storage proof whose path passes through this node.
            for (int i = 0; i < _fullStoragePaths.Length; i++)
            {
                if (IsPrefix(_fullStoragePaths[i], ctx.Path))
                    _storageProofItems[i].Add(rlp);
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

        /// <summary>
        /// Returns true if <paramref name="fullPath"/> exactly equals <paramref name="targetPath"/>.
        /// Used at the account leaf where the context path already includes the leaf's key.
        /// </summary>
        private static bool IsFullPathMatch(Nibble[] targetPath, TreePath fullPath)
        {
            if (fullPath.Length != targetPath.Length) return false;
            for (int i = 0; i < targetPath.Length; i++)
            {
                if (fullPath[i] != (byte)targetPath[i]) return false;
            }
            return true;
        }
    }
}
