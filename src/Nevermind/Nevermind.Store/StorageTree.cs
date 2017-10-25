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

        public byte[] Get(BigInteger position)
        {
            return Get(Keccak.Compute(position.ToBigEndianByteArray()).Bytes);
        }

        public void Set(BigInteger position, byte[] value)
        {
            Set(Keccak.Compute(position.ToBigEndianByteArray()).Bytes, value);
        }
    }
}