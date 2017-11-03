using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class StorageTree : PatriciaTree
    {
        public StorageTree(InMemoryDb db) : base(db)
        {
        }

        public StorageTree(Keccak rootHash, InMemoryDb db) : base(rootHash, db)
        {
        }

        private byte[] GetKey(BigInteger index)
        {
            return Keccak.Compute(index.ToBigEndianByteArray(true, 32)).Bytes;
        }

        public byte[] Get(BigInteger index)
        {
            byte[] value = Get(GetKey(index));
            if (value == null)
            {
                return new byte[] {0};
            }
            Rlp rlp = new Rlp(value);
            return (byte[])Rlp.Decode(rlp);
        }

        public void Set(BigInteger index, byte[] value)
        {
            Set(GetKey(index), Rlp.Encode(value));
        }
    }
}