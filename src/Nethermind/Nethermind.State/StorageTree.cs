// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StorageTree : IPatriciaTree
    {
        private static readonly UInt256 CacheSize = 1024;

        private static readonly int CacheSizeInt = (int)CacheSize;

        private static readonly Dictionary<UInt256, byte[]> Cache = new(CacheSizeInt);

        private readonly PatriciaTree _patriciaTree;

        static StorageTree()
        {
            Span<byte> buffer = stackalloc byte[32];
            for (int i = 0; i < CacheSizeInt; i++)
            {
                UInt256 index = (UInt256)i;
                index.ToBigEndian(buffer);
                Cache[index] = Keccak.Compute(buffer).BytesToArray();
            }
        }

        [DebuggerStepThrough]
        public StorageTree(ITrieStore? store, ILogManager? logManager)
            : this(store, Keccak.EmptyTreeHash, logManager)
        {
        }

        [DebuggerStepThrough]
        public StorageTree(ITrieStore? store, Keccak rootHash, ILogManager? logManager)
            : this(new PatriciaTree(store, rootHash, false, true, logManager) { TrieType = TrieType.Storage })
        {
        }

        [DebuggerStepThrough]
        public StorageTree(PatriciaTree patriciaTree)
        {
            if (patriciaTree.TrieType != TrieType.Storage) throw new ArgumentException($"{nameof(PatriciaTree.TrieType)} must be {nameof(TrieType.Storage)}", nameof(patriciaTree));
            _patriciaTree = patriciaTree;
        }

        public Keccak RootHash
        {
            get
            {
                return _patriciaTree.RootHash;
            }
            set
            {
                _patriciaTree.RootHash = value;
            }
        }

        public TrieNode? RootRef
        {
            get
            {
                return _patriciaTree.RootRef;
            }
            set
            {
                _patriciaTree.RootRef = value;
            }
        }

        public ITrieStore TrieStore => _patriciaTree.TrieStore;

        private static void GetKey(in UInt256 index, in Span<byte> key)
        {
            if (index < CacheSize)
            {
                Cache[index].CopyTo(key);
            }
            else
            {
                index.ToBigEndian(key);

                // in situ calculation
                KeccakHash.ComputeHashBytesToSpan(key, key);
            }
        }


        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, Keccak? storageRoot = null)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(index, key);

            byte[]? value = _patriciaTree.Get(key, storageRoot);
            return value is null ? new byte[] { 0 } : value.AsRlpValueContext().DecodeByteArray();
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, byte[] value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(index, key);
            SetInternal(key, value);
        }

        public void Set(in ValueKeccak key, byte[] value, bool rlpEncode = true)
        {
            SetInternal(key.Bytes, value, rlpEncode);
        }

        private void SetInternal(ReadOnlySpan<byte> rawKey, byte[] value, bool rlpEncode = true)
        {
            if (value.IsZero())
            {
                _patriciaTree.Set(rawKey, Array.Empty<byte>());
            }
            else
            {
                Rlp rlpEncoded = rlpEncode ? Rlp.Encode(value) : new Rlp(value);
                _patriciaTree.Set(rawKey, rlpEncoded);
            }
        }

        public void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None)
        {
            _patriciaTree.Commit(blockNumber, skipRoot, writeFlags);
        }

        public void UpdateRootHash()
        {
            _patriciaTree.UpdateRootHash();
        }

        public void Set(ReadOnlySpan<byte> rawKey, byte[] value)
        {
            _patriciaTree.Set(rawKey, value);
        }

        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, Rlp? value)
        {
            Set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
        }
    }
}
