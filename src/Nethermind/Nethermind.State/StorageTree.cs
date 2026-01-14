// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StorageTree : PatriciaTree, IWorldStateScopeProvider.IStorageTree
    {
        private const int LookupSize = 1024;
        private static readonly FrozenDictionary<UInt256, ValueHash256> Lookup = CreateLookup();
        public static readonly byte[] ZeroBytes = [0];

        private static FrozenDictionary<UInt256, ValueHash256> CreateLookup()
        {
            Span<byte> buffer = stackalloc byte[32];
            Dictionary<UInt256, ValueHash256> lookup = new Dictionary<UInt256, ValueHash256>(LookupSize);
            for (int i = 0; i < LookupSize; i++)
            {
                UInt256 index = new UInt256((uint)i);
                index.ToBigEndian(buffer);
                lookup[index] = ValueKeccak.Compute(buffer);
            }

            return lookup.ToFrozenDictionary();
        }

        public StorageTree(IScopedTrieStore? trieStore, ILogManager? logManager)
            : this(trieStore, Keccak.EmptyTreeHash, logManager)
        {
        }

        public StorageTree(IScopedTrieStore? trieStore, Hash256 rootHash, ILogManager? logManager)
            : base(trieStore, rootHash, true, logManager)
        {
            TrieType = TrieType.Storage;
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ComputeKey(in UInt256 index, out ValueHash256 key)
        {
            Span<byte> buffer = stackalloc byte[32];
            index.ToBigEndian(buffer);
            KeccakCache.ComputeTo(buffer, out key);
        }

        public static void ComputeKeyWithLookup(in UInt256 index, ref ValueHash256 key)
        {
            if (index < LookupSize)
            {
                key = Lookup[index];
                return;
            }

            ComputeKey(index, out key);
        }

        public static BulkSetEntry CreateBulkSetEntry(in ValueHash256 key, byte[]? value)
        {
            byte[] encodedValue;
            if (value.IsZero())
            {
                encodedValue = [];
            }
            else
            {
                Rlp rlpEncoded = Rlp.Encode(value);
                if (rlpEncoded is null)
                {
                    encodedValue = [];
                }
                else
                {
                    encodedValue = rlpEncoded.Bytes;
                }
            }

            return new BulkSetEntry(in key, encodedValue);
        }

        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, Hash256? storageRoot = null)
        {
            if (index < LookupSize)
            {
                return GetArray(Lookup[index].Bytes, storageRoot);
            }

            return GetWithKeyGenerate(in index, storageRoot);

            [SkipLocalsInit]
            byte[] GetWithKeyGenerate(in UInt256 index, Hash256 storageRoot)
            {
                ComputeKey(index, out ValueHash256 key);
                return GetArray(key.Bytes, storageRoot);
            }
        }

        public byte[] GetArray(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> value = Get(rawKey, rootHash);

            if (value.IsEmpty)
            {
                return ZeroBytes;
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            return rlp.DecodeByteArray();
        }

        public void Commit()
        {
            Commit(false, WriteFlags.None);
        }

        public void Clear()
        {
            RootHash = EmptyTreeHash;
        }

        public bool WasEmptyTree => RootHash == EmptyTreeHash;

        public byte[] Get(in UInt256 index)
        {
            return Get(index, null);
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
        }

        public byte[] Get(in ValueHash256 hash)
        {
            return GetArray(hash.Bytes, null);
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, byte[] value)
        {
            if (index < LookupSize)
            {
                SetInternal(Lookup[index].Bytes, value);
            }
            else
            {
                SetWithKeyGenerate(in index, value);
            }

            [SkipLocalsInit]
            void SetWithKeyGenerate(in UInt256 index, byte[] value)
            {
                ComputeKey(index, out ValueHash256 key);
                SetInternal(key.Bytes, value);
            }
        }

        public void Set(in ValueHash256 key, byte[] value, bool rlpEncode = true)
        {
            SetInternal(key.Bytes, value, rlpEncode);
        }

        private void SetInternal(ReadOnlySpan<byte> rawKey, byte[] value, bool rlpEncode = true)
        {
            if (value.IsZero())
            {
                Set(rawKey, []);
            }
            else
            {
                Rlp rlpEncoded = rlpEncode ? Rlp.Encode(value) : new Rlp(value);
                Set(rawKey, rlpEncoded);
            }
        }
    }
}
