using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    public class RefsJournalTests
    {
        private IRefsCache _refsCache;
        private ILogManager _logManager;
        private ILogger _logger;

        [SetUp]
        public void Setup()
        {
            _logManager = new OneLoggerLogManager(new NUnitLogger(LogLevel.Trace));
            _logger = _logManager?.GetClassLogger();
            _refsCache = new RefsCache(_logManager);
        }
        
        [Test]
        public void Default_capacity_is_128()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.Capacity.Should().Be(128);
        }

        [Test]
        public void Cannot_unwind_if_no_books_present()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            Assert.Throws<InvalidOperationException>(() => refsJournal.Unwind());
        }

        [Test]
        public void Cannot_unwind_unsealed()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            Assert.Throws<InvalidOperationException>(() => refsJournal.Unwind());
        }
        
        [Test]
        public void Will_drop_books_beyond_capacity()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            for (int i = 0; i < refsJournal.Capacity * 2; i++)
            {
                refsJournal.StartNewBook(TestItem.KeccakFromNumber(i));
                refsJournal.SealBook();
            }
            
            for (int i = 0; i < refsJournal.Capacity; i++)
            {
                refsJournal.Unwind();
            }
            
            Assert.Throws<InvalidOperationException>(() => refsJournal.Unwind());
        }

        [Test]
        public void Can_seal_empty_book()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
        }

        [Test]
        public void Can_seal_non_empty_book()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.RecordEntry(Keccak.Zero, 3);
            refsJournal.SealBook();
        }

        [Test]
        public void Can_unwind_sealed_and_empty()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            book.IsSealed.Should().Be(true);
        }

        [Test]
        public void Cannot_rewind_twice()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            refsJournal.Rewind(book);
            Assert.Throws<InvalidOperationException>(() => refsJournal.Rewind(book));
        }

        [Test]
        public void Unwound_book_has_correct_number_of_entries()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Zero);
            trieNode.Refs = 6;
            
            _refsCache.Add(trieNode.Keccak!, trieNode);
            
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.RecordEntry(Keccak.Zero, 3);
            refsJournal.SealBook();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(1));
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            book.Entries.Should().HaveCount(0);
            book = refsJournal.Unwind();
            book.Entries.Should().HaveCount(3);
        }
        
        [Test]
        public void Rewound_book_has_correct_number_of_entries()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Zero);
            trieNode.Refs = 6;
            
            _refsCache.Add(trieNode.Keccak!, trieNode);
            
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.RecordEntry(Keccak.Zero, 3);
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            book.Entries.Should().HaveCount(3);
            refsJournal.Rewind(book);
            book = refsJournal.Unwind();
            book.Entries.Should().HaveCount(3);
        }

        [Test]
        public void Can_start_new()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
        }

        [Test]
        public void Can_start_new_if_previous_had_no_records()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(1));
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(2));
        }

        [Test]
        public void Can_start_new_if_previous_had_records()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(1));
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(2));
        }

        [Test]
        public void Does_not_care_about_0_or_negative_or_very_big_numbers()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 0);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(1));
            refsJournal.RecordEntry(Keccak.Zero, -1);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(2));
            refsJournal.RecordEntry(Keccak.Zero, int.MinValue);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(3));
            refsJournal.RecordEntry(Keccak.Zero, int.MaxValue);
        }

        [Test]
        public void Can_keep_winding_unwinding()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();

            for (int i = 0; i < 64; i++)
            {
                var book = refsJournal.Unwind();
                refsJournal.Rewind(book);
            }
        }
        
        [Test]
        public void Cannot_seal_sealed()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
            Assert.Throws<InvalidOperationException>(() => refsJournal.SealBook());
        }
        
        [Test]
        public void Cannot_record_when_no_book()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            Assert.Throws<InvalidOperationException>(() => refsJournal.RecordEntry(Keccak.Zero, 1));
        }
        
        [Test]
        public void Cannot_record_when_book_is_sealed()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            refsJournal.StartNewBook(Keccak.Zero);
            refsJournal.SealBook();
            Assert.Throws<InvalidOperationException>(() => refsJournal.RecordEntry(Keccak.Zero, 1));
        }
        
        [Test]
        public void Cannot_seal_when_no_books()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            Assert.Throws<InvalidOperationException>(() => refsJournal.SealBook());
        }
        
        [Test]
        public void Cannot_rewind_null()
        {
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            Assert.Throws<ArgumentNullException>(() => refsJournal.Rewind(null));
        }

        [Test]
        public void Fuzz_unwind_rewind()
        {
            int numberOfBlocks = _random.Next(4, 64);
            int numberOfInitialNodes = _random.Next(4, 2048);

            Dictionary<Keccak, int> initialRefs = new Dictionary<Keccak, int>();
            Dictionary<Keccak, int> expectedRefs = new Dictionary<Keccak, int>();
            
            IRefsJournal refsJournal = new RefsJournal(_refsCache, _logManager);
            IRefsReorganizer refsReorganizer = new RefsReorganizer(refsJournal, _refsCache);
            
            for (int i = 0; i < numberOfInitialNodes; i++)
            {
                var keccak = TestItem.KeccakFromNumber(i);
                TrieNode trieNode = new TrieNode(NodeType.Unknown, keccak);
                trieNode.Refs = _random.Next(0, 4);
                _logger.Trace($"{keccak} refs to {trieNode.Refs} (Init)");
                _refsCache.Add(trieNode.Keccak!, trieNode);
                initialRefs[trieNode.Keccak] = trieNode.Refs;
            }

            int booksCount = 0;
            for (int i = 0; i < numberOfBlocks; i++)
            {
                _logger.Trace($"{i}");
                int numberOfChangesInTheBlock = _random.Next(0, 128);

                bool shouldRewind = _random.Next(2) == 0;
                int reorgDepth = _random.Next(Math.Min(booksCount, 16));
                if (!shouldRewind)
                {
                    booksCount -= reorgDepth;
                }
                
                Reorganize(refsReorganizer, refsJournal, reorgDepth, shouldRewind);
                
                booksCount++;
                AddOneFuzzBook(
                    refsJournal, _refsCache, expectedRefs, i, numberOfChangesInTheBlock, numberOfInitialNodes);
            }

            for (int i = 0; i < numberOfBlocks; i++)
            {
                int reorgDepth = _random.Next(Math.Min(booksCount, i));
                Reorganize(refsReorganizer, refsJournal, reorgDepth, true);
            }
            
            for (int i = 0; i < booksCount; i++)
            {
                refsJournal.Unwind();
            }
            
            foreach ((Keccak key, int value) in initialRefs)
            {
                _logger.Trace($"Checking {key} - expected {value}, is {_refsCache.Get(key).Refs}");
                _refsCache.Get(key).Refs.Should().Be(value);
            }
        }

        private void Reorganize(
            IRefsReorganizer refsReorganizer,
            IRefsJournal refsJournal,
            int reorgDepth,
            bool shouldRewind)
        {
            _logger.Trace($"Reorganizing with depth {reorgDepth}");
            // refsReorganizer.MoveBack();

            Stack<JournalBook> unwindStack
                = new Stack<JournalBook>();
            for (int reorgBlockIndex = 0; reorgBlockIndex < reorgDepth; reorgBlockIndex++)
            {
                var book = refsJournal.Unwind();
                unwindStack.Push(book);
            }
            
            if (shouldRewind)
            {
                while (unwindStack.TryPop(out JournalBook book))
                {
                    refsJournal.Rewind(book);
                }
            }
        }

        private void AddOneFuzzBook(
            IRefsJournal refsJournal,
            IRefsCache refsCache,
            Dictionary<Keccak, int> refsForChecks,
            int bookIndex,
            int numberOfChangesInTheBook,
            int numberOfInitialNodes)
        {
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(bookIndex));
            for (int j = 0; j < numberOfChangesInTheBook; j++)
            {
                int actionType = _random.Next(3);
                Keccak key;
                TrieNode node = null;
                int refsChange;
                switch (actionType)
                {
                    case 0: // new node
                        key = TestItem.KeccakFromNumber(numberOfInitialNodes + j * bookIndex);
                        if (refsForChecks.ContainsKey(key))
                        {
                            continue;
                        }
                        
                        node = new TrieNode(NodeType.Unknown, key);
                        node.Refs = _random.Next(4);
                        refsCache.Add(key, node);
                        refsChange = node.Refs;
                        refsJournal.RecordEntry(node.Keccak!, node.Refs);
                        _logger.Trace($"New journal entry {key} refs to {node.Refs} ({refsChange}) (New)");
                        break;
                    case 1: // update node
                        key = TestItem.KeccakFromNumber(_random.Next(numberOfInitialNodes));
                        node = refsCache.Get(key);
                        int previousRefs = node.Refs; 
                        node.Refs = _random.Next(4);
                        refsChange = node.Refs - previousRefs;
                        refsJournal.RecordEntry(node.Keccak!, refsChange);
                        _logger.Trace($"New journal entry {key} refs to {node.Refs} ({refsChange}) (Update)");
                        break;
                    case 2: // delete
                        key = TestItem.KeccakFromNumber(_random.Next(numberOfInitialNodes));
                        node = refsCache.Get(key);
                        refsChange = -node.Refs;
                        node.Refs = 0;
                        refsJournal.RecordEntry(node.Keccak!, refsChange);
                        refsCache.Remove(key);
                        _logger.Trace($"New journal entry {key} refs to {node.Refs} ({refsChange}) (Delete)");
                        break;
                }

                if (node != null)
                {
                    refsForChecks[node.Keccak!] = node.Refs;
                }
            }

            refsJournal.SealBook();
        }
        
        private static Random _random = new Random();
    }
}