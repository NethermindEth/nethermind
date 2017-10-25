using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class SecurePatriciaTree : PatriciaTree
    {
        public SecurePatriciaTree(Keccak rootHash, InMemoryDb db)
            : base(rootHash, db)
        {
        }

        public SecurePatriciaTree(InMemoryDb db)
            :base(db)
        {
        }

        public override void Set(Nibble[] rawKey, byte[] value)
        {
            Keccak keccak = Keccak.Compute(rawKey.ToPackedByteArray());
            base.Set(Nibbles.FromBytes(keccak.Bytes), value);
        }
    }
}