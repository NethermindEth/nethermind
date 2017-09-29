using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class SecurePatriciaTree : PatriciaTree
    {
        public SecurePatriciaTree(Keccak rootHash, Db db)
            : base(rootHash, db)
        {
        }

        public SecurePatriciaTree(Db db)
            :base(db)
        {
        }

        public override void Set(byte[] rawKey, byte[] value)
        {
            base.Set(Keccak.Compute(rawKey).Bytes, value);
        }
    }
}