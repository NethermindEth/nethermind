using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class JournalBook
    {
        public JournalBook()
        {
            IsSealed = false;
                
            _entries = new List<JournalEntry>();
        }
            
        private readonly List<JournalEntry> _entries;

        public IEnumerable<JournalEntry> Entries => _entries;

        /// <summary>
        /// Defines whether any records can still be added.
        /// </summary>
        public bool IsSealed { get; internal set; }
        
        public bool IsUnwound { get; internal set; }

        internal void RecordEntry(JournalEntry journalEntry)
        {
            _entries.Add(journalEntry);
        }

        public override string ToString()
        {
            return $"[{nameof(JournalBook)}|{_entries.Count}|sealed:{IsSealed}|unwound:{IsUnwound}]";
        }
    }
}