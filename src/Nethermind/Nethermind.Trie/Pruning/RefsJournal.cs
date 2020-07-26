using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class RefsJournal : IRefsJournal
    {
        public RefsJournal(int capacity = 128)
        {
            Capacity = capacity;
        }
        
        public int Capacity { get; }

        public void StartNewBook()
        {
            throw new NotImplementedException();
        }

        public void RecordEntry(Keccak hash, int refs)
        {
            throw new NotImplementedException();
        }

        public void SealBook()
        {
            throw new NotImplementedException();
        }

        public IRefsJournal.JournalBook Unwind()
        {
            throw new NotImplementedException();
        }

        public void Rewind(IRefsJournal.JournalBook journalBook)
        {
            throw new NotImplementedException();
        }
    }
}