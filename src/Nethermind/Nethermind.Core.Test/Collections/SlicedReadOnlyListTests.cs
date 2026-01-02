// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [TestFixture]
    public class SlicedReadOnlyListTests
    {
        private List<int> _original;

        [SetUp]
        public void SetUp()
        {
            _original = new List<int> { 0, 1, 2, 3, 4, 5 };
        }

        [Test]
        public void Slice_WithStartAndCount_ReturnsCorrectSlice()
        {
            // Slicing from index 2 and taking 3 elements should return {2, 3, 4}.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(2, 3);
            slice.Count.Should().Be(3);
            slice.Should().Equal(new List<int> { 2, 3, 4 });
        }

        [Test]
        public void Slice_WithOnlyStart_ReturnsSliceToEnd()
        {
            // Slicing from index 3 to the end should return {3, 4, 5}.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(3);
            slice.Count.Should().Be(3);
            slice.Should().Equal(new List<int> { 3, 4, 5 });
        }

        [Test]
        public void Slice_InvalidParameters_ThrowsArgumentOutOfRangeException()
        {
            var readOnly = _original.AsReadOnly();

            // Negative start index
            Action actNegativeStart = () => readOnly.Slice(-1, 2);
            actNegativeStart.Should().Throw<ArgumentOutOfRangeException>();

            // Count too high
            Action actCountTooHigh = () => readOnly.Slice(2, 10);
            actCountTooHigh.Should().Throw<ArgumentOutOfRangeException>();

            // Start index out of range
            Action actStartOutOfRange = () => readOnly.Slice(10, 1);
            actStartOutOfRange.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void Indexer_ReturnsCorrectValues()
        {
            // Slice from index 1 for 4 elements: expected slice is {1, 2, 3, 4}.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(1, 4);
            slice[0].Should().Be(1);
            slice[3].Should().Be(4);

            // Attempting to access an out-of-range index should throw an exception.
            Action actOutOfRange = () => { var _ = slice[4]; };
            actOutOfRange.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void Enumerator_ReturnsAllElementsInSlice()
        {
            // Verify the enumerator returns all the expected elements.
            IReadOnlyList<int> slice = _original.AsReadOnly().Slice(2, 3);
            var enumeratedList = slice.ToList();
            enumeratedList.Should().Equal(new List<int> { 2, 3, 4 });
        }
    }
}
