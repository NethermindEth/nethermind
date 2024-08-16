// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.State
{
    public class StorageTree : PatriciaTree
    {
        private const int LookupSize = 1024;
        private static readonly FrozenDictionary<UInt256, byte[]> Lookup = CreateLookup();
        public static readonly byte[] EmptyBytes = [0];

        private static FrozenDictionary<UInt256, byte[]> CreateLookup()
        {
            Span<byte> buffer = stackalloc byte[32];
            Dictionary<UInt256, byte[]> lookup = new Dictionary<UInt256, byte[]>(LookupSize);
            for (int i = 0; i < LookupSize; i++)
            {
                UInt256 index = (UInt256)i;
                index.ToBigEndian(buffer);
                lookup[index] = Keccak.Compute(buffer).BytesToArray();
            }

            return lookup.ToFrozenDictionary();
        }

        public StorageTree(IScopedTrieStore? trieStore, ILogManager? logManager)
            : this(trieStore, Keccak.EmptyTreeHash, logManager)
        {
        }

        public StorageTree(IScopedTrieStore? trieStore, Hash256 rootHash, ILogManager? logManager)
            : base(trieStore, rootHash, false, true, logManager)
        {
            TrieType = TrieType.Storage;
        }

        [ThreadStatic]
        private static byte[] _key;
        private static byte[] GetKeyArray() => _key ??= new byte[32];

        [SkipLocalsInit]
        private static Span<byte> ComputeKey(in UInt256 index)
        {
            byte[] key = GetKeyArray();
            index.ToBigEndian(key);

            ValueHash256 keyHash = Keccak.Compute(key);

            Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(key))
                = Unsafe.As<ValueHash256, Vector256<byte>>(ref keyHash);

            return key;
        }

        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, Hash256? storageRoot = null)
        {
            if (index < LookupSize)
            {
                return GetArray(Lookup[index], storageRoot);
            }

            return GetArray(ComputeKey(index), storageRoot);
        }

        public byte[] GetArray(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> value = base.Get(rawKey, rootHash);

            if (value.IsEmpty)
            {
                return EmptyBytes;
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            return rlp.DecodeByteArray();
        }

        public override ReadOnlySpan<byte> Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null) => GetArray(rawKey, rootHash);

        [SkipLocalsInit]
        public void Set(in UInt256 index, byte[] value)
        {
            if (index < LookupSize)
            {
                SetInternal(Lookup[index], value);
            }
            else
            {
                SetInternal(ComputeKey(index), value);
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
                Set(rawKey, Array.Empty<byte>());
            }
            else
            {
                Rlp rlpEncoded = rlpEncode ? Rlp.Encode(value) : new Rlp(value);
                Set(rawKey, rlpEncoded);
            }
        }
    }
}
