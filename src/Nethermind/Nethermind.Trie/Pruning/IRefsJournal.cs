using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface IRefsJournal
    {
        int Capacity { get; }

        void StartNewBook(Keccak hash);
        
        void RecordEntry(Keccak hash, int refs);
        
        void SealBook();

        JournalBook Unwind();
        
        void Rewind(JournalBook journalBook);
    }
}