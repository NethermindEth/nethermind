// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using System.Runtime.InteropServices;

namespace Nethermind.State
{
    public class StorageTree : PatriciaTree, IWorldStateScopeProvider.IStorageTree
    {
        private static readonly ValueHash256[] Lookup = CreateLookup();
        public static readonly byte[] ZeroBytes = [0];

        private static ValueHash256[] CreateLookup()
        {
            const int LookupSize = 1024;

            Span<byte> buffer = stackalloc byte[32];
            ValueHash256[] lookup = new ValueHash256[LookupSize];

            for (int i = 0; i < lookup.Length; i++)
            {
                UInt256 index = new UInt256((uint)i);
                index.ToBigEndian(buffer);
                lookup[i] = ValueKeccak.Compute(buffer);
            }

            return lookup;
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
            // Cannot use key as both in and out to KeccakCache.ComputeTo,
            // so create another 32-byte buffer
            Unsafe.SkipInit(out ValueHash256 buffer);
            index.ToBigEndian(buffer.BytesAsSpan);
            KeccakCache.ComputeTo(buffer.Bytes, out key);
        }

        [SkipLocalsInit]
        public static void ComputeKeyWithLookup(in UInt256 index, ref ValueHash256 key)
        {
            ValueHash256[] lookup = Lookup;
            ulong u0 = index.u0;
            if (index.IsUint64 && u0 < (uint)lookup.Length)
            {
                key = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(lookup), (nuint)u0);
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

        public static BulkSetEntry CreateBulkSetEntry(in ValueHash256 key, in StorageValue value)
        {
            if (value.IsZero)
            {
                return new BulkSetEntry(in key, []);
            }

            return new BulkSetEntry(in key, RlpEncodeStorageValue(in value));
        }

        [SkipLocalsInit]
        public StorageValue GetStorageValue(in UInt256 index, Hash256? storageRoot = null)
        {
            ValueHash256[] lookup = Lookup;
            ulong u0 = index.u0;
            if (index.IsUint64 && u0 < (uint)lookup.Length)
            {
                return GetStorageValue(
                    in Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(lookup), (nuint)u0),
                    storageRoot);
            }

            return GetWithKeyGenerate(in index, storageRoot);

            [SkipLocalsInit]
            StorageValue GetWithKeyGenerate(in UInt256 index, Hash256 storageRoot)
            {
                ComputeKey(index, out ValueHash256 key);
                return GetStorageValue(in key, storageRoot);
            }
        }

        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, Hash256? storageRoot = null)
        {
            return GetStorageValue(in index, storageRoot).ToEvmBytes();
        }

        public StorageValue GetStorageValue(in ValueHash256 key, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> rawKey = key.Bytes;
            ReadOnlySpan<byte> value = Get(rawKey, rootHash);

            if (value.IsEmpty)
            {
                return StorageValue.Zero;
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            ReadOnlySpan<byte> decoded = rlp.DecodeByteArraySpan();
            return StorageValue.FromSpanWithoutLeadingZero(decoded);
        }

        public byte[] GetArray(in ValueHash256 key, Hash256? rootHash = null)
        {
            return GetStorageValue(in key, rootHash).ToEvmBytes();
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

        StorageValue IWorldStateScopeProvider.IStorageTree.Get(in UInt256 index)
        {
            return GetStorageValue(index);
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
        }

        StorageValue IWorldStateScopeProvider.IStorageTree.Get(in ValueHash256 hash)
        {
            return GetStorageValue(in hash, null);
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, byte[] value)
        {
            ValueHash256[] lookup = Lookup;
            ulong u0 = index.u0;
            if (index.IsUint64 && u0 < (uint)lookup.Length)
            {
                SetInternal(in Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(lookup), (nuint)u0), value);
            }
            else
            {
                SetWithKeyGenerate(in index, value);
            }

            [SkipLocalsInit]
            void SetWithKeyGenerate(in UInt256 index, byte[] value)
            {
                ComputeKey(index, out ValueHash256 key);
                SetInternal(in key, value);
            }
        }

        public void Set(in ValueHash256 key, byte[] value, bool rlpEncode = true)
        {
            SetInternal(in key, value, rlpEncode);
        }

        private void SetInternal(in ValueHash256 hash, byte[] value, bool rlpEncode = true)
        {
            ReadOnlySpan<byte> rawKey = hash.Bytes;
            if (value.IsZero())
            {
                Set(rawKey, []);
            }
            else if (rlpEncode)
            {
                Set(rawKey, Rlp.Encode(value));
            }
            else
            {
                Set(rawKey, value);
            }
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, in StorageValue value)
        {
            ValueHash256[] lookup = Lookup;
            ulong u0 = index.u0;
            if (index.IsUint64 && u0 < (uint)lookup.Length)
            {
                SetInternal(in Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(lookup), (nuint)u0), in value);
            }
            else
            {
                SetWithKeyGenerate(in index, in value);
            }

            [SkipLocalsInit]
            void SetWithKeyGenerate(in UInt256 index, in StorageValue value)
            {
                ComputeKey(index, out ValueHash256 key);
                SetInternal(in key, in value);
            }
        }

        private void SetInternal(in ValueHash256 hash, in StorageValue value)
        {
            ReadOnlySpan<byte> rawKey = hash.Bytes;
            if (value.IsZero)
            {
                Set(rawKey, []);
            }
            else
            {
                byte[] rlpEncoded = RlpEncodeStorageValue(in value);
                Set(rawKey, rlpEncoded);
            }
        }

        /// <summary>
        /// RLP-encodes a StorageValue directly into a single byte[] allocation,
        /// avoiding the intermediate byte[] from ToEvmBytes() and the Rlp wrapper.
        /// </summary>
        [SkipLocalsInit]
        private static byte[] RlpEncodeStorageValue(in StorageValue value)
        {
            ReadOnlySpan<byte> trimmed = value.AsReadOnlySpan.WithoutLeadingZeros();
            int rlpLength = Rlp.LengthOf(trimmed);
            byte[] result = GC.AllocateUninitializedArray<byte>(rlpLength);
            Rlp.Encode(result.AsSpan(), 0, trimmed);
            return result;
        }
    }
}
