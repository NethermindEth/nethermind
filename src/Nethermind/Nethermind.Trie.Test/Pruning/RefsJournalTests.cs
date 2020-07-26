using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
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
            refsJournal.StartNewBook();
            Assert.Throws<InvalidOperationException>(() => refsJournal.Unwind());
        }
        
        [Test]
        public void Can_seal_empty_book()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.SealBook();
        }
        
        [Test]
        public void Can_seal_non_empty_book()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.RecordEntry(Keccak.Zero, 3);
            refsJournal.SealBook();
        }

        [Test]
        public void Can_unwind_sealed_and_empty()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            book.IsSealed.Should().Be(true);
        }
        
        [Test]
        public void Cannot_rewind_twice()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            refsJournal.Rewind(book);
            Assert.Throws<InvalidOperationException>(() => refsJournal.Rewind(book));
        }
        
        [Test]
        public void Unwound_book_has_correct_number_of_entries()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.RecordEntry(Keccak.Zero, 3);
            refsJournal.SealBook();
            refsJournal.StartNewBook();
            refsJournal.SealBook();
            var book = refsJournal.Unwind();
            book.Entries.Should().HaveCount(3);
            book = refsJournal.Unwind();
            book.Entries.Should().HaveCount(0);
        }
        
        [Test]
        public void Can_start_new()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
        }
        
        [Test]
        public void Can_start_new_if_previous_had_no_records()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.StartNewBook();
            refsJournal.StartNewBook();
        }
        
        [Test]
        public void Can_start_new_if_previous_had_records()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, 1);
            refsJournal.RecordEntry(Keccak.Zero, 2);
            refsJournal.StartNewBook();
            refsJournal.StartNewBook();
        }
        
        [Test]
        public void Does_not_care_about_0_or_negative_or_very_big_numbers()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, 0);
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, -1);
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, int.MinValue);
            refsJournal.StartNewBook();
            refsJournal.RecordEntry(Keccak.Zero, int.MaxValue);
        }
        
        [Test]
        public void Can_keep_winding_unwinding()
        {
            IRefsJournal refsJournal = new RefsJournal();
            refsJournal.StartNewBook();
            refsJournal.SealBook();

            for (int i = 0; i < 64; i++)
            {
                var book = refsJournal.Unwind();
                refsJournal.Rewind(book);    
            }
        }
    }
}