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

        private ulong _outstandingWritesEstimate;

        public ulong OutstandingWritesEstimate => _outstandingWritesEstimate;
        public void ClearWritesEstimate() => _outstandingWritesEstimate = 0;

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

        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, Hash256? storageRoot = null)
        {
            ValueHash256[] lookup = Lookup;
            ulong u0 = index.u0;
            if (index.IsUint64 && u0 < (uint)lookup.Length)
            {
                return GetArray(
                    in Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(lookup), (nuint)u0),
                    storageRoot);
            }

            return GetWithKeyGenerate(in index, storageRoot);

            [SkipLocalsInit]
            byte[] GetWithKeyGenerate(in UInt256 index, Hash256 storageRoot)
            {
                ComputeKey(index, out ValueHash256 key);
                return GetArray(in key, storageRoot);
            }
        }

        public byte[] GetArray(in ValueHash256 key, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> rawKey = key.Bytes;
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
            return GetArray(in hash, null);
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
            _outstandingWritesEstimate++;
            ReadOnlySpan<byte> rawKey = hash.Bytes;
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

        internal void IncrementEstimate(ulong count = 1)
            => _outstandingWritesEstimate += count;
    }
}
