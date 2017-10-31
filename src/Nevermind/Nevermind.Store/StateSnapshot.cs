using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class StateSnapshot
    {
        public StateSnapshot(InMemoryDb db, Keccak rootHash)
        {
            DbSnapshot = db;
            RootHash = rootHash;
        }

        internal InMemoryDb DbSnapshot { get; }

        internal Keccak RootHash { get; set; }
    }
}