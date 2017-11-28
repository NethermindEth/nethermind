using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Store
{
    public class SecurePatriciaTree : PatriciaTree
    {
        public SecurePatriciaTree(Keccak rootHash, InMemoryDb db)
            : base(db, rootHash)
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