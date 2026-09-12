// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
    public partial class StorageTree : PatriciaTree, IWorldStateScopeProvider.IStorageTree
    {
        public static readonly byte[] ZeroBytes = [0];

        public StorageTree(IScopedTrieStore trieStore, ILogManager logManager)
            : this(trieStore, Keccak.EmptyTreeHash, logManager)
        {
        }

        public StorageTree(IScopedTrieStore trieStore, Hash256 rootHash, ILogManager logManager)
            : base(trieStore, rootHash, true, logManager) => TrieType = TrieType.Storage;

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

        private static byte[] EncodeNonZeroValue(ReadOnlySpan<byte> value)
        {
            byte[] encoded = GC.AllocateUninitializedArray<byte>(Rlp.LengthOf(value));
            Rlp.Encode(value, encoded);
            return encoded;
        }

        public static BulkSetEntry CreateBulkSetEntry(in ValueHash256 key, ReadOnlySpan<byte> value) =>
            new(in key, value.IsZero() ? [] : EncodeNonZeroValue(value));

        public void Commit() => Commit(false, WriteFlags.None);

        public void Clear() => RootHash = EmptyTreeHash;

        public bool WasEmptyTree => RootHash == EmptyTreeHash;

        public void Get(in UInt256 index, out UInt256 value) => Get(in index, out value, null);

        internal void Get(in UInt256 index, out UInt256 value, Hash256? storageRoot)
        {
            ValueHash256 key = default;
            ComputeKeyWithLookup(in index, ref key);
            ReadOnlySpan<byte> encoded = Get(key.Bytes, storageRoot);
            if (encoded.IsEmpty)
            {
                value = default;
                return;
            }
            RlpReader reader = new(encoded);
            ReadOnlySpan<byte> decoded = reader.DecodeByteArraySpan();
            if (decoded.Length > 32) throw new TrieException("Storage value exceeds 256 bits");
            value = new UInt256(decoded, isBigEndian: true);
        }

        public void HintSet(in UInt256 index)
        {
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, ReadOnlySpan<byte> value)
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
            void SetWithKeyGenerate(in UInt256 index, ReadOnlySpan<byte> value)
            {
                ComputeKey(index, out ValueHash256 key);
                SetInternal(in key, value);
            }
        }

        public void Set(in ValueHash256 key, byte[] value, bool rlpEncode = true)
        {
            if (rlpEncode)
            {
                SetInternal(in key, value);
            }
            else if (value.IsZero())
            {
                Set(key.Bytes, []);
            }
            else
            {
                Set(key.Bytes, new CappedArray<byte>(value));
            }
        }

        private void SetInternal(in ValueHash256 hash, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> rawKey = hash.Bytes;
            if (value.IsZero())
            {
                Set(rawKey, []);
            }
            else
            {
                // Bind the CappedArray overload the Rlp one used to forward to, so a non-zero write
                // keeps bypassing the virtual byte[] entry point that HealingStorageTree overrides.
                Set(rawKey, new CappedArray<byte>(EncodeNonZeroValue(value)));
            }
        }
    }
}
