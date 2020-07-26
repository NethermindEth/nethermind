using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class RefsJournal : IRefsJournal
    {
        private LinkedList<JournalBook> _books
            = new LinkedList<JournalBook>();
        
        public RefsJournal(int capacity = 128)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public void StartNewBook(Keccak hash)
        {
            JournalBook journalBook = new JournalBook(hash);
            _books.AddLast(new LinkedListNode<JournalBook>(journalBook));
        }

        public void RecordEntry(Keccak hash, int refs)
        {
            if (_books.Any() && !_books.Last!.Value.IsSealed)
            {
                _books.Last.Value.RecordEntry(hash, refs);
            }
            else
            {
                throw new InvalidOperationException(
                    "Trying to add an entry while there is no open book.");
            }
        }

        public void SealBook()
        {
            if (_books.Any() && !_books.Last!.Value.IsSealed)
            {
                _books.Last.Value.IsSealed = true;
            }
            else
            {
                throw new InvalidOperationException(
                    "An attempt to seal a book while there is no open book.");
            }
        }

        /// <summary>
        /// There is a big chance that the return value is not needed
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public JournalBook Unwind()
        {
            JournalBook unwoundBook;
            if (_books.Any() && _books.Last!.Value.IsSealed)
            {
                unwoundBook = _books.Last.Value;
                _books.RemoveLast();
            }
            else
            {
                throw new InvalidOperationException(
                    "An attempt to unwind a book while there is no open book.");
            }

            unwoundBook.IsUnwound = true;
            return unwoundBook;
        }

        /// <summary>
        /// There is a big chance that this is not needed
        /// </summary>
        /// <param name="journalBook"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void Rewind(JournalBook journalBook)
        {
            if (journalBook is null)
            {
                throw new ArgumentNullException(nameof(journalBook));
            }
            
            if (!journalBook.IsUnwound)
            {
                throw new InvalidOperationException(
                    "Cannot rewind a book that is not unwound.");
            }
            
            journalBook.IsUnwound = false;
            _books.AddLast(new LinkedListNode<JournalBook>(journalBook));
        }
    }
}