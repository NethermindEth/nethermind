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

namespace Nethermind.State
{
    public class StorageTree : PatriciaTree
    {
        private const int LookupSize = 1024;
        private static readonly FrozenDictionary<UInt256, byte[]> Lookup = CreateLookup();
        private static readonly byte[] _emptyBytes = [0];

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

        private static void ComputeKey(in UInt256 index, ref Span<byte> key)
        {
            index.ToBigEndian(key);

            // in situ calculation
            KeccakHash.ComputeHashBytesToSpan(key, key);
        }

        [SkipLocalsInit]
        public byte[] Get(in UInt256 index, Hash256? storageRoot = null)
        {
            if (index < LookupSize)
            {
                return Get(Lookup[index], storageRoot).ToArray();
            }

            Span<byte> key = stackalloc byte[32];
            ComputeKey(index, ref key);
            return Get(key, storageRoot).ToArray();

        }

        public override ReadOnlySpan<byte> Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> value = base.Get(rawKey, rootHash);

            if (value.IsEmpty)
            {
                return _emptyBytes;
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            return rlp.DecodeByteArray();
        }

        [SkipLocalsInit]
        public void Set(in UInt256 index, byte[] value)
        {
            if (index < LookupSize)
            {
                SetInternal(Lookup[index], value);
            }
            else
            {
                Span<byte> key = stackalloc byte[32];
                ComputeKey(index, ref key);
                SetInternal(key, value);
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
