using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public readonly struct JournalEntry
    {
        public JournalEntry(Keccak hash, int refs)
        {
            Hash = hash;
            Refs = refs;
        }

        public Keccak Hash { get; }
            
        public int Refs { get; }

        public override string ToString()
        {
            return $"[{nameof(JournalEntry)}|{Hash}|{Refs}]";
        }
    }
}