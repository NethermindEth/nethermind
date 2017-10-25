using System;
using Nevermind.Core;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class StateTree : PatriciaTree
    {
        public StateTree(InMemoryDb db) : base(db)
        {
        }

        public StateTree(Keccak rootHash, InMemoryDb db) : base(rootHash, db)
        {
        }

        public Rlp Get(Address address)
        {
            throw new NotImplementedException();
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