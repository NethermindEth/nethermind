//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
    public class CanonicalHashTrie: PatriciaTree
    {
        private static readonly ChtDecoder _decoder = new();
        public static readonly int SectionSize = 32768; // 2**15

        private static readonly byte[] MaxSectionKey = Encoding.ASCII.GetBytes("MaxSection");

        public CanonicalHashTrie(IKeyValueStoreWithBatching db)
            : base(db, GetMaxRootHash(db), true, true, NullLogManager.Instance)
        {
        }

        public CanonicalHashTrie(IKeyValueStoreWithBatching db, Keccak rootHash)
            : base(db, rootHash, true, true, NullLogManager.Instance)
        {
        }

        public void CommitSectionIndex(long sectionIndex)
        {
            StoreRootHash(sectionIndex);
        }

        public long GetMaxSectionIndex()
        {
            //return GetMaxSectionIndex(_keyValueStore);
            return -1;
        }

        public static long GetSectionFromBlockNo(long blockNo) => (blockNo / SectionSize) - 1L;

        public byte[][] BuildProof(long blockNo, long sectionIndex, long fromLevel)
        {
            return BuildProof(GetKey(blockNo), sectionIndex, fromLevel);
        }

        public byte[][] BuildProof(byte[] key, long sectionIndex, long fromLevel)
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

        private void SetMaxSectionIndex(long sectionIndex)
        {
            //_keyValueStore[MaxSectionKey] = sectionIndex.ToBigEndianByteArrayWithoutLeadingZeros();
        }

        //private Keccak GetRootHash(long sectionIndex)
        //{
        //    //return GetRootHash(_keyValueStore, sectionIndex);
        //}

        private static Keccak GetRootHash(IKeyValueStore db, long sectionIndex)
        {
            byte[]? hash = db[GetRootHashKey(sectionIndex)];
            return hash == null ? EmptyTreeHash : new Keccak(hash);
        }

        private static Keccak GetMaxRootHash(IKeyValueStore db)
        {
            long maxSection = GetMaxSectionIndex(db);
            return maxSection == 0L ? EmptyTreeHash : GetRootHash(db, maxSection);
        }

        public void Set(BlockHeader header)
        {
            Set(GetKey(header), GetValue(header));
        }

        public (Keccak?, UInt256) Get(long key)
        {
            return Get(GetKey(key));
        }
        
        public (Keccak?, UInt256) Get(Span<byte> key)
        {
            byte[]? val = base.Get(key);
            if (val == null)
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

        private Rlp GetValue(BlockHeader header)
        {
            if (!header.TotalDifficulty.HasValue)
            {
                throw new ArgumentException("Trying to use a header with a null total difficulty in LES Canonical Hash Trie") ;
            }

            return _decoder.Encode((header.Hash, header.TotalDifficulty.Value));
        }
    }
}
