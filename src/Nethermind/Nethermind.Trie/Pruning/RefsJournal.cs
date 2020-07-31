using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class RefsJournal : IRefsJournal
    {
        private readonly ITrieNodeCache _trieNodeCache;

        private LinkedList<JournalBook> _books
            = new LinkedList<JournalBook>();

        private ILogger _logger;

        public RefsJournal(ITrieNodeCache trieNodeCache, ILogManager logManager, int capacity = 128)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _trieNodeCache = trieNodeCache ?? throw new ArgumentNullException(nameof(trieNodeCache));
            Capacity = capacity;
        }

        public int Capacity { get; }

        public void StartNewBook()
        {
            JournalBook book = new JournalBook();
            if (_logger.IsDebug) _logger.Debug($"New journal book  {book}");
            _books.AddLast(new LinkedListNode<JournalBook>(book));
        }

        public void RecordEntry(Keccak hash, int refs)
        {
            if (hash == null)
            {
                throw new ArgumentNullException(nameof(hash));
            }
            
            if (!_books.Any())
            {
                throw new InvalidOperationException(
                    "An attempt to add an entry there are no books in journal.");
            }

            if (_books.Last!.Value.IsSealed)
            {
                throw new InvalidOperationException(
                    "An attempt to add an entry while the book has already been sealed.");
            }

            JournalEntry entry = new JournalEntry(hash, refs);
            if(_logger.IsTrace) _logger.Trace($"New entry         {entry}.");
            _books.Last.Value.RecordEntry(entry);
        }

        public void SealBook()
        {
            if (!_books.Any())
            {
                throw new InvalidOperationException(
                    "An attempt to seal a book while there are no books in journal.");
            }

            if (_books.Last!.Value.IsSealed)
            {
                throw new InvalidOperationException(
                    "An attempt to seal a book that has already been sealed.");
            }

            if(_logger.IsDebug) _logger.Debug($"Sealing book      {_books.Last!.Value}");
            _books.Last.Value.IsSealed = true;

            if (_books.Count > Capacity)
            {
                _books.RemoveFirst();
            }
        }

        /// <summary>
        /// There is a big chance that the return value is not needed
        /// </summary>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public JournalBook Unwind()
        {
            if (!_books.Any())
            {
                throw new InvalidOperationException(
                    "An attempt to unwind a book while there are no books in journal.");
            }
            
            if (!_books.Last!.Value.IsSealed)
            {
                throw new InvalidOperationException(
                    "An attempt to unwind a book that has not been sealed.");
            }
            
            JournalBook book = _books.Last.Value;
            if (_logger.IsDebug) _logger.Debug($"Unwinding book    {book}");
            
            _books.RemoveLast();
            book.IsUnwound = true;
            var newRefsBook = _books.Last!.Value;
            
            foreach (JournalEntry entry in newRefsBook.Entries.Reverse())
            {
                if(_logger.IsTrace) _logger.Debug($"Unwinding         {entry}");
                _trieNodeCache.Get(entry.Hash).Refs -= entry.Refs;
            }
            
            return book;
        }

        /// <summary>
        /// There is a big chance that this is not needed
        /// </summary>
        /// <param name="book"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void Rewind(JournalBook book)
        {
            if (book is null)
            {
                throw new ArgumentNullException(nameof(book));
            }
            
            if (!book.IsUnwound)
            {
                throw new InvalidOperationException(
                    "Cannot rewind a book that is not unwound.");
            }
            
            if (_logger.IsDebug) _logger.Debug($"Rewinding book    {book}");
            
            book.IsUnwound = false;
            _books.AddLast(new LinkedListNode<JournalBook>(book));
            foreach (JournalEntry entry in book.Entries)
            {
                if(_logger.IsTrace) _logger.Debug($"Rewinding         {entry}");
                _trieNodeCache.Get(entry.Hash).Refs += entry.Refs;
            }
        }
    }
}