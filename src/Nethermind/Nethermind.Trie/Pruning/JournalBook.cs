using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class JournalBook
    {
        public JournalBook(Keccak bookHash)
        {
            BookHash = bookHash;
            IsSealed = false;
                
            _entries = new List<JournalEntry>();
        }
            
        private readonly List<JournalEntry> _entries;

        public IEnumerable<JournalEntry> Entries => _entries;

        /// <summary>
        /// Is meant to be blockhash really
        /// </summary>
        public Keccak BookHash { get; }
            
        /// <summary>
        /// Defines whether any records can still be added.
        /// </summary>
        public bool IsSealed { get; internal set; }
        
        public bool IsUnwound { get; internal set; }

        internal void RecordEntry(Keccak hash, int refs)
        {
            _entries.Add(new JournalEntry(hash, refs));
        }
    }
}