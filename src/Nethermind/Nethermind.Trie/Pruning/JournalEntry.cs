using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public readonly struct JournalEntry
    {
        public JournalEntry(Keccak hash, int refs)
        {
            Node = null;
            Hash = hash;
            Refs = refs;
        }
        
        public JournalEntry(TrieNode node, int refs)
        {
            Node = node;
            Hash = null;
            Refs = refs;
        }

        public Keccak Hash { get; }
        
        public TrieNode Node { get; }
            
        public int Refs { get; }

        public override string ToString()
        {
            return $"[{nameof(JournalEntry)}|{Hash}|{Refs}]";
        }
    }
}