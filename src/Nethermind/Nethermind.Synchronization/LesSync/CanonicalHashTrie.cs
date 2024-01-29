// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Synchronization.LesSync
{
    public class CanonicalHashTrie : PatriciaTree
    {
        private static readonly ChtDecoder _decoder = new();
        public static readonly int SectionSize = 32768; // 2**15

        private static readonly byte[] MaxSectionKey = Encoding.ASCII.GetBytes("MaxSection");

        public CanonicalHashTrie(IKeyValueStoreWithBatching db)
            : base(db, GetMaxRootHash(db), true, true, NullLogManager.Instance)
        {
        }

        public CanonicalHashTrie(IKeyValueStoreWithBatching db, Hash256 rootHash)
            : base(db, rootHash, true, true, NullLogManager.Instance)
        {
        }

        public void CommitSectionIndex(long sectionIndex)
        {
            StoreRootHash(sectionIndex);
        }

        public static long GetMaxSectionIndex()
        {
            //return GetMaxSectionIndex(_keyValueStore);
            return -1;
        }

        public static long GetSectionFromBlockNo(long blockNo) => (blockNo / SectionSize) - 1L;

        public static byte[][] BuildProof(long blockNo, long sectionIndex, long fromLevel)
        {
            return BuildProof(GetKey(blockNo), sectionIndex, fromLevel);
        }

        public static byte[][] BuildProof(byte[] key, long sectionIndex, long fromLevel)
        {
            ChtProofCollector proofCollector = new(key, fromLevel);
            //Accept(proofCollector, GetRootHash(sectionIndex), false);
            return proofCollector.BuildResult();
        }

        private void StoreRootHash(long sectionIndex)
        {
            UpdateRootHash();
            //_keyValueStore[GetRootHashKey(sectionIndex)] = RootHash.Bytes;
            //if (GetMaxSectionIndex(_keyValueStore) < sectionIndex)
            //{
            //    SetMaxSectionIndex(sectionIndex);
            //}
        }

        private static long GetMaxSectionIndex(IKeyValueStore db)
        {
            byte[]? storeValue = null;
            try
            {
                storeValue = db[MaxSectionKey];
            }
            catch (KeyNotFoundException) { }
            return storeValue?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? -1L;
        }

        private static Hash256 GetRootHash(IKeyValueStore db, long sectionIndex)
        {
            byte[]? hash = db[GetRootHashKey(sectionIndex)];
            return hash is null ? EmptyTreeHash : new Hash256(hash);
        }

        private static Hash256 GetMaxRootHash(IKeyValueStore db)
        {
            long maxSection = GetMaxSectionIndex(db);
            return maxSection == 0L ? EmptyTreeHash : GetRootHash(db, maxSection);
        }

        public void Set(BlockHeader header)
        {
            Set(GetKey(header), GetValue(header));
        }

        public (Hash256?, UInt256) Get(long key)
        {
            return Get(GetKey(key));
        }

        public (Hash256?, UInt256) Get(Span<byte> key)
        {
            ReadOnlySpan<byte> val = base.Get(key);
            if (val.IsEmpty)
            {
                throw new InvalidDataException("Missing CHT data");
            }

            return _decoder.Decode(val);
        }

        private static byte[] GetKey(BlockHeader header)
        {
            return GetKey(header.Number);
        }

        private static byte[] GetKey(long key)
        {
            return key.ToBigEndianByteArrayWithoutLeadingZeros().PadLeft(8);
        }

        private static byte[] GetRootHashKey(long key)
        {
            return Bytes.Concat(Encoding.ASCII.GetBytes("RootHash"), GetKey(key));
        }

        private static Rlp GetValue(BlockHeader header)
        {
            if (!header.TotalDifficulty.HasValue)
            {
                throw new ArgumentException("Trying to use a header with a null total difficulty in LES Canonical Hash Trie");
            }

            (Hash256? Hash, UInt256 Value) item = (header.Hash, header.TotalDifficulty.Value);
            RlpStream stream = new(_decoder.GetLength(item, RlpBehaviors.None));
            _decoder.Encode(stream, item);
            return new Rlp(stream.Data.ToArray());
        }
    }
}
