﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State
{
    public class StorageTree : PatriciaTree
    {
        private static readonly UInt256 CacheSize = 1024;

        private static readonly int CacheSizeInt = (int)CacheSize;

        private static readonly Dictionary<UInt256, byte[]> Cache = new Dictionary<UInt256, byte[]>(CacheSizeInt);

        static StorageTree()
        {
            Span<byte> buffer = stackalloc byte[32];
            for (int i = 0; i < CacheSizeInt; i++)
            {
                UInt256 index = (UInt256)i;
                index.ToBigEndian(buffer);
                Cache[index] = Keccak.Compute(buffer).Bytes;
            }
        }

        public StorageTree(IDb db) : base(db, Keccak.EmptyTreeHash, false, true)
        {
        }

        public StorageTree(IDb db, Keccak rootHash) : base(db, rootHash, false, true)
        {
        }
        
        public static Span<byte> GetKey(UInt256 index)
        {
            if (index < CacheSize)
            {
                return Cache[index];
            }

            Span<byte> span = stackalloc byte[32];
            index.ToBigEndian(span);
            return ValueKeccak.Compute(span).BytesAsSpan.ToArray();
        }

        public byte[] Get(UInt256 index, Keccak storageRoot = null)
        {
            Span<byte> key = GetKey(index);
            byte[] value = Get(key, storageRoot);
            if (value == null)
            {
                return new byte[] {0};
            }

            Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
            return rlp.DecodeByteArray();
        }

        public void Set(UInt256 index, byte[] value)
        {
            if (value.IsZero())
            {
                Set(GetKey(index), Bytes.Empty);
            }
            else
            {
                Set(GetKey(index), Rlp.Encode(value));
            }
        }
    }
}