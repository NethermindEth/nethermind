using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface IRefsJournal
    {
        public readonly struct JournalEntry
        {
            public JournalEntry(TrieNode trieNode, Keccak hash, int refs)
            {
                Hash = hash;
                Info = refs;
            }

            public Keccak Hash { get; }
            
            public int Info { get; }
        }
        
        public struct JournalBook
        {
            // some rentable array
            
            public IEnumerable<JournalEntry> Entries { get; }
            
            public bool IsSealed { get; }

            internal void RecordEntry(Keccak hash, int refs)
            {
                throw new NotImplementedException();
            }
        }

        int Capacity { get; }

        void StartNewBook();
        
        void RecordEntry(Keccak hash, int refs);
        
        void SealBook();

        JournalBook Unwind();
        
        void Rewind(JournalBook journalBook);
    }
}