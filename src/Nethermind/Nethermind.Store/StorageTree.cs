/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public class StorageTree : PatriciaTree
    {
        private static readonly BigInteger CacheSize = 8;

        private static readonly int CacheSizeInt = (int)CacheSize;

        private static readonly Dictionary<BigInteger, byte[]> Cache = new Dictionary<BigInteger, byte[]>(CacheSizeInt);

        static StorageTree()
        {
            for (int i = 0; i < CacheSizeInt; i++)
            {
                Cache[i] = Keccak.Compute(new BigInteger(i).ToBigEndianByteArray(32)).Bytes;
            }
        }

        public StorageTree(IDb db) : base(db)
        {
        }

        public StorageTree(IDb db, Keccak rootHash) : base(db, rootHash)
        {
        }

        private byte[] GetKey(BigInteger index)
        {
            if (index < CacheSize)
            {
                return Cache[index];
            }

            return Keccak.Compute(index.ToBigEndianByteArray(32)).Bytes;
        }

        public byte[] Get(BigInteger index)
        {
            byte[] key = GetKey(index);
            byte[] value = Get(key);
            if (value == null)
            {
                return new byte[] {0};
            }

            Rlp.DecoderContext rlp = value.AsRlpContext();
            return rlp.DecodeByteArray();
        }

        public void Set(BigInteger index, byte[] value)
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