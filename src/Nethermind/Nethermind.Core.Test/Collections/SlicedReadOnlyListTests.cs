// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [TestFixture]
    public class SlicedReadOnlyListTests
    {
        private List<int> _original;

        [SetUp]
        public void SetUp() => _original = [0, 1, 2, 3, 4, 5];

        [Test]
        public void Slice_WithStartAndCount_ReturnsCorrectSlice()
        {
            // Slicing from index 2 and taking 3 elements should return {2, 3, 4}.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(2, 3);
            Assert.That(slice.Count, Is.EqualTo(3));
            Assert.That(slice, Is.EqualTo(new List<int> { 2, 3, 4 }));
        }

        [Test]
        public void Slice_WithOnlyStart_ReturnsSliceToEnd()
        {
            // Slicing from index 3 to the end should return {3, 4, 5}.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(3);
            Assert.That(slice.Count, Is.EqualTo(3));
            Assert.That(slice, Is.EqualTo(new List<int> { 3, 4, 5 }));
        }

        [Test]
        public void Slice_InvalidParameters_ThrowsArgumentOutOfRangeException()
        {
            ReadOnlyCollection<int> readOnly = _original.AsReadOnly();

            // Negative start index
            Action actNegativeStart = () => readOnly.Slice(-1, 2);
            Assert.That(actNegativeStart, Throws.TypeOf<ArgumentOutOfRangeException>());

            // Count too high
            Action actCountTooHigh = () => readOnly.Slice(2, 10);
            Assert.That(actCountTooHigh, Throws.TypeOf<ArgumentOutOfRangeException>());

            // Start index out of range
            Action actStartOutOfRange = () => readOnly.Slice(10, 1);
            Assert.That(actStartOutOfRange, Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Indexer_ReturnsCorrectValues()
        {
            // Slice from index 1 for 4 elements: expected slice is {1, 2, 3, 4}.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(1, 4);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(slice[0], Is.EqualTo(1));
                Assert.That(slice[3], Is.EqualTo(4));
            }

            // Attempting to access an out-of-range index should throw an exception.
            Action actOutOfRange = () => { int _ = slice[4]; };
            Assert.That(actOutOfRange, Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Enumerator_ReturnsAllElementsInSlice()
        {
            // Verify the enumerator returns all the expected elements.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(2, 3);
            List<int> enumeratedList = slice.ToList();
            Assert.That(enumeratedList, Is.EqualTo(new List<int> { 2, 3, 4 }));
        }
    }
}
