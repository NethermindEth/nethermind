using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public readonly struct JournalEntry
    {
        public JournalEntry(Keccak hash, int refsChange)
        {
            Hash = hash;
            RefsChange = refsChange;
        }

        public Keccak Hash { get; }
            
        public int RefsChange { get; }

        public override string ToString()
        {
            return $"[{nameof(JournalEntry)}|{Hash}|{RefsChange}]";
        }
    }
}