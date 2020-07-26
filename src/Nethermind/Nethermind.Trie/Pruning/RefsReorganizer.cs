using System;
using System.Collections.Generic;
using System.ComponentModel;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// There is a big chance that this class is not needed
    /// </summary>
    public class RefsReorganizer : IRefsReorganizer
    {
        private readonly IRefsJournal _refsJournal;
        private readonly IRefsCache _refsCache;

        private Dictionary<Keccak, JournalBook> _cachedBooks
            = new Dictionary<Keccak, JournalBook>();

        public RefsReorganizer(IRefsJournal refsJournal, IRefsCache refsCache)
        {
            _refsJournal = refsJournal ?? throw new ArgumentNullException(nameof(refsJournal));
            _refsCache = refsCache ?? throw new ArgumentNullException(nameof(refsCache));
        }

        public void MoveBack(params Keccak[] hashes)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                var book = _refsJournal.Unwind();
                if (book.BookHash != hashes[i])
                {
                    throw new InvalidAsynchronousStateException(
                        $"Unwound book hash {book.BookHash} is not matching the hash {hashes[i]}.");
                }

                _cachedBooks.Add(book.BookHash, book);
            }
        }
    }
}