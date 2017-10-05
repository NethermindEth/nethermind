using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class StateTree : PatriciaTree
    {
        public StateTree(Db db) : base(db)
        {
        }

        public StateTree(Keccak rootHash, Db db) : base(rootHash, db)
        {
        }

        public void Set(Address address, Rlp rlp)
        {
            Set(Keccak.Compute((byte[])address.Hex), rlp);
        }

        public void Set(Keccak addressHash, Rlp rlp)
        {
            base.Set(addressHash.Bytes, rlp);
        }
    }
}