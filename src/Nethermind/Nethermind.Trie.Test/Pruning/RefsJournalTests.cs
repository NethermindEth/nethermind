using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    public class RefsJournalTests
    {
        [Test]
        public void Default_capacity_is_128()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.Capacity.Should().Be(128);
        }

        [Test]
        public void Cannot_unwind_if_no_books_present()
        {
            IRefsJournal refsJournal = new RefsJournal();
            Assert.Throws<InvalidOperationException>(() => refsJournal.Unwind());
        }

        [Test]
        public void Cannot_unwind_unsealed()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            Assert.Throws<InvalidOperationException>(() => refsJournal.Unwind());
        }

        [Test]
        public void Can_seal_empty_book()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
        }

        [Test]
        public void Can_seal_non_empty_book()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.RecordEntry(Keccak.Zero, 3);
            refsJournal.SealBook();
        }

        [Test]
        public void Can_unwind_sealed_and_empty()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            book.IsSealed.Should().Be(true);
        }

        [Test]
        public void Cannot_rewind_twice()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            refsJournal.Rewind(book);
            Assert.Throws<InvalidOperationException>(() => refsJournal.Rewind(book));
        }

        [Test]
        public void Unwound_book_has_correct_number_of_entries()
        {
            IRefsJournal refsJournal = new RefsJournal();
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
            IRefsJournal refsJournal = new RefsJournal();
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
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
        }

        [Test]
        public void Can_start_new_if_previous_had_no_records()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(1));
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(2));
        }

        [Test]
        public void Can_start_new_if_previous_had_records()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(1));
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(2));
        }

        [Test]
        public void Does_not_care_about_0_or_negative_or_very_big_numbers()
        {
            IRefsJournal refsJournal = new RefsJournal();
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
            IRefsJournal refsJournal = new RefsJournal();
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
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(TestItem.KeccakFromNumber(0));
            refsJournal.SealBook();
            Assert.Throws<InvalidOperationException>(() => refsJournal.SealBook());
        }
        
        [Test]
        public void Cannot_record_when_no_book()
        {
            IRefsJournal refsJournal = new RefsJournal();
            Assert.Throws<InvalidOperationException>(() => refsJournal.RecordEntry(Keccak.Zero, 1));
        }
        
        [Test]
        public void Cannot_record_when_book_is_sealed()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook(Keccak.Zero);
            refsJournal.SealBook();
            Assert.Throws<InvalidOperationException>(() => refsJournal.RecordEntry(Keccak.Zero, 1));
        }
        
        [Test]
        public void Cannot_seal_when_no_books()
        {
            IRefsJournal refsJournal = new RefsJournal();
            Assert.Throws<InvalidOperationException>(() => refsJournal.SealBook());
        }
        
        [Test]
        public void Cannot_rewind_null()
        {
            IRefsJournal refsJournal = new RefsJournal();
            Assert.Throws<ArgumentNullException>(() => refsJournal.Rewind(null));
        }

        [Test]
        public void Fuzz_unwind_rewind()
        {
            int numberOfBlocks = _random.Next(4, 2048);
            int numberOfInitialNodes = _random.Next(4, 2048);

            Dictionary<Keccak, int> expectedRefs = new Dictionary<Keccak, int>();
            
            IRefsJournal refsJournal = new RefsJournal();
            IRefsCache refsCache = new RefsCache();
            IRefsReorganizer refsReorganizer = new RefsReorganizer(refsJournal, refsCache);
            
            for (int i = 0; i < numberOfInitialNodes; i++)
            {
                var keccak = TestItem.KeccakFromNumber(i);
                TrieNode trieNode = new TrieNode(NodeType.Unknown, keccak);
                trieNode.Refs = _random.Next(0, 4);
                refsCache.Add(trieNode.Keccak, trieNode);
                expectedRefs[trieNode.Keccak] = trieNode.Refs;
            }

            for (int i = 0; i < numberOfBlocks; i++)
            {
                int numberOfChangesInTheBlock = _random.Next(0, 128);
                AddOneFuzzBook(
                    refsJournal, refsCache, expectedRefs, i, numberOfChangesInTheBlock, numberOfInitialNodes);
                
                bool shouldRewind = _random.Next(2) == 0;
                Reorganize(refsReorganizer, refsJournal, i, shouldRewind);
            }

            foreach ((Keccak key, int value) in expectedRefs)
            {
                refsCache.Get(key).Refs.Should().Be(value);
            }

            for (int i = 0; i < 1024; i++)
            {
                Reorganize(refsReorganizer, refsJournal, i, true);
            }
        }

        private static void Reorganize(
            IRefsReorganizer refsReorganizer,
            IRefsJournal refsJournal,
            int i,
            bool shouldRewind)
        {
            int reorgDepth = _random.Next(0, Math.Min(i, 16));
            refsReorganizer.MoveBack();

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

        private static void AddOneFuzzBook(
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
                switch (actionType)
                {
                    case 0: // new node
                        var keccak = TestItem.KeccakFromNumber(numberOfInitialNodes + j * bookIndex);
                        TrieNode trieNode = new TrieNode(NodeType.Unknown, keccak);
                        refsCache.Add(keccak, trieNode);
                        trieNode.Refs = _random.Next(4);
                        refsForChecks[trieNode.Keccak] = trieNode.Refs;
                        refsJournal.RecordEntry(trieNode.Keccak, trieNode.Refs);
                        break;
                    case 1: // update node
                        int nodeTpUpdateIndex = _random.Next(numberOfInitialNodes);
                        var keccakOfNodeToUpdate = TestItem.KeccakFromNumber(nodeTpUpdateIndex);
                        var nodeToUpdate = refsCache.Get(keccakOfNodeToUpdate);
                        nodeToUpdate.Refs = _random.Next(4);
                        refsForChecks[nodeToUpdate.Keccak] = nodeToUpdate.Refs;
                        refsJournal.RecordEntry(nodeToUpdate.Keccak, nodeToUpdate.Refs);
                        break;
                    case 2: // delete
                        int nodeToDeleteIndex = _random.Next(numberOfInitialNodes);
                        var keccakOfNodeToDelete = TestItem.KeccakFromNumber(nodeToDeleteIndex);
                        var nodeToDelete = refsCache.Get(keccakOfNodeToDelete);
                        nodeToDelete.Refs = 0;
                        refsForChecks[nodeToDelete.Keccak] = nodeToDelete.Refs;
                        refsCache.Remove(keccakOfNodeToDelete);
                        refsJournal.RecordEntry(nodeToDelete.Keccak, nodeToDelete.Refs);
                        break;
                }
            }

            refsJournal.SealBook();
        }
        
        private static Random _random = new Random();
    }
}