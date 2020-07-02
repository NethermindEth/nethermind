using System.Collections.Generic;
using Nethermind.Ssz;

namespace Nethermind.Merkleization
{
    public class MemMerkleTreeStore : IKeyValueStore<ulong, byte[]>
    {
        private Dictionary<ulong, byte[]?> _dictionary = new Dictionary<ulong, byte[]?>();
        
        public byte[]? this[ulong key]
        {
            get => _dictionary.ContainsKey(key) ? _dictionary[key] : null;
            set => _dictionary[key] = value;
        }
    }
}