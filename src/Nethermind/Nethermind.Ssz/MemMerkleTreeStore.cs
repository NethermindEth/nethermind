using System.Collections.Generic;

namespace Nethermind.Ssz
{
    public class MemMerkleTreeStore : IKeyValueStore<uint, byte[]>
    {
        private Dictionary<uint, byte[]?> _dictionary = new Dictionary<uint, byte[]?>();
        
        public byte[]? this[uint key]
        {
            get => _dictionary.ContainsKey(key) ? _dictionary[key] : null;
            set => _dictionary[key] = value;
        }
    }
}