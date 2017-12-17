using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Store
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

            Rlp rlp = new Rlp(value);
            return (byte[])Rlp.Decode(rlp);
        }

        public void Set(BigInteger index, byte[] value)
        {
            if (value.IsZero())
            {
                Set(GetKey(index), new byte[] { });
            }
            else
            {
                Set(GetKey(index), Rlp.Encode(value));
            }
        }
    }
}