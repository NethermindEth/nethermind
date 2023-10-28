// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StorageTree : PatriciaTree
    {
        private static readonly UInt256 CacheSizeU = CacheSize;

        public const int CacheSize = 1024;

        private static readonly byte[][] Cache = new byte[CacheSize][];

        private KeyHashCache _cache;

        static StorageTree()
        {
            Span<byte> buffer = stackalloc byte[32];
            for (int i = 0; i < CacheSize; i++)
            {
                UInt256 index = (UInt256)i;
                index.ToBigEndian(buffer);
                Cache[(int)index] = Keccak.Compute(buffer).BytesToArray();
            }
        }

        public StorageTree(ITrieStore? trieStore, ILogManager? logManager)
            : base(trieStore, Keccak.EmptyTreeHash, false, true, logManager)
        {
            TrieType = TrieType.Storage;
        }

        public StorageTree(ITrieStore? trieStore, Hash256 rootHash, ILogManager? logManager)
            : base(trieStore, rootHash, false, true, logManager)
        {
            TrieType = TrieType.Storage;
        }

        private void GetKey(in UInt256 index, in Span<byte> key, KeyHints hint)
        {
            if (index < CacheSizeU)
            {
                Cache[(int)index].CopyTo(key);
                return;
            }

            if (hint == KeyHints.Set)
            {
                // if set try to find first as it's likely it was read first
                if (_cache.TryGet(in index, key))
                {
                    return;
                }
            }
            else if (hint == KeyHints.GetMightBeFollowedByWrite)
            {
                // get might be followed by write, try cache the input
                index.ToBigEndian(key);
                KeccakHash.ComputeHashBytesToSpan(key, key);
                _cache.Set(in index, key);

                return;
            }

            index.ToBigEndian(key);

            // in situ calculation
            KeccakHash.ComputeHashBytesToSpan(key, key);
        }

        enum KeyHints
        {
            GetMightBeFollowedByWrite,
            GetProbablyNotFollowedByWrite,
            Set,
        }

        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, bool potentialWriteNext = false, Hash256? storageRoot = null)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(index, key,
                potentialWriteNext ? KeyHints.GetMightBeFollowedByWrite : KeyHints.GetProbablyNotFollowedByWrite);

            byte[]? value = Get(key, storageRoot);
            if (value is null)
            {
                return new byte[] { 0 };
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            return rlp.DecodeByteArray();
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, byte[] value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(index, key, KeyHints.Set);
            SetInternal(key, value);
        }

        public void ResetHashCache() => _cache = default;

        public void Set(in ValueHash256 key, byte[] value, bool rlpEncode = true)
        {
            SetInternal(key.Bytes, value, rlpEncode);
        }

        private void SetInternal(ReadOnlySpan<byte> rawKey, byte[] value, bool rlpEncode = true)
        {
            if (value.IsZero())
            {
                Set(rawKey, Array.Empty<byte>());
            }
            else
            {
                Rlp rlpEncoded = rlpEncode ? Rlp.Encode(value) : new Rlp(value);
                Set(rawKey, rlpEncoded);
            }
        }

        struct KeyHashCache
        {
            private const int Size = 16;
            private Cell[]? _cells = null;

            public KeyHashCache()
            {
            }

            struct Cell
            {
                public int HashCode;
                public UInt256 Value;
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
                public ValueHash256 Hash;
#pragma warning restore CS0649 // Field is never assigned to, and will always have its default value
            }

            public void Set(in UInt256 value, Span<byte> hash)
            {
                _cells ??= new Cell[Size];

                int cellHashCode = value.GetHashCode();
                ref Cell cell = ref _cells![cellHashCode % Size];

                cell.HashCode = cellHashCode;
                cell.Value = value;
                hash.CopyTo(cell.Hash.BytesAsSpan);
            }

            public bool TryGet(in UInt256 value, Span<byte> hash)
            {
                if (_cells == null)
                    return false;

                int cellHashCode = value.GetHashCode();
                ref readonly Cell cell = ref _cells![cellHashCode % Size];

                if (cell.HashCode == cellHashCode && cell.Value == value)
                {
                    cell.Hash.BytesAsSpan.CopyTo(hash);
                    return true;
                }

                return false;
            }
        }
    }
}
