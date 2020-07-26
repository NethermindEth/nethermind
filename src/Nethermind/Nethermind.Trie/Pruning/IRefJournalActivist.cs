using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface IRefJournalActivist
    {
        void Archive(Keccak hash, IRefsJournal.JournalBook book);
        
        IRefsJournal.JournalBook Bring(Keccak hash);
    }
}