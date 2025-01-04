// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
public class TrieKeyTests
{
    [Test]
    public void Empty_ShouldHaveLengthZero_AndBeEquivalentToEmptyArray()
    {
        // Arrange & Act
        TrieKey emptyKey = TrieKey.Empty;

        // Assert
        emptyKey.Length.Should().Be(0);
        emptyKey.ToArray().Should().BeEmpty();
    }

    [Test]
    public void CreateFromByte_ShouldBeSingleByteKey()
    {
        // Arrange
        byte singleByte = 5;

        // Act
        TrieKey trieKey = singleByte; // Implicit operator

        // Assert
        trieKey.Length.Should().Be(1);
        trieKey[0].Should().Be(singleByte);
        trieKey.ToArray().Should().Equal(new byte[] { singleByte });
    }

    [Test]
    public void CreateFromByteArray_ShouldStoreAllBytes()
    {
        // Arrange
        byte[] bytes = new byte[] { 10, 11, 12 };

        // Act
        TrieKey trieKey = bytes; // Implicit operator from byte[]

        // Assert
        trieKey.Length.Should().Be(3);
        trieKey.ToArray().Should().Equal(bytes);
        trieKey[0].Should().Be(10);
        trieKey[1].Should().Be(11);
        trieKey[2].Should().Be(12);
    }

    [Test]
    public void CreateFromSingleByteAndTrieKey_ShouldPrependByte()
    {
        // Arrange
        byte prependByte = 4;
        TrieKey baseKey = new byte[] { 5, 6 };

        // Act
        TrieKey combined = new(prependByte, baseKey);

        // Assert
        combined.Length.Should().Be(3);
        combined.ToArray().Should().Equal(new byte[] { 4, 5, 6 });
    }

    [Test]
    public void CreateFromTwoTrieKeys_ShouldConcatenate()
    {
        // Arrange
        TrieKey leftKey = new byte[] { 1, 2, 3 };
        TrieKey rightKey = new byte[] { 4, 5 };

        // Act
        TrieKey concatenated = new(leftKey, rightKey);

        // Assert
        concatenated.Length.Should().Be(5);
        concatenated.ToArray().Should().Equal(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    public void Indexer_ShouldThrowIfIndexOutOfRange()
    {
        // Arrange
        TrieKey key = new byte[] { 1, 2, 3 };

        // Act
        Action action1 = () => { var _ = key[-1]; };
        Action action2 = () => { var _ = key[3]; };

        // Assert
        action1.Should().Throw<ArgumentOutOfRangeException>();
        action2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void CommonPrefixLength_ShouldReturnCorrectValue_WhenKeysSharePrefix()
    {
        // Arrange
        TrieKey key1 = new byte[] { 1, 2, 3, 4 };
        TrieKey key2 = new byte[] { 1, 2, 7, 8 };

        // Act
        int prefixLength = key1.CommonPrefixLength(key2);

        // Assert
        prefixLength.Should().Be(2, "the first two bytes match (1, 2)");
    }

    [Test]
    public void CommonPrefixLength_ShouldReturnZero_WhenNoSharedPrefix()
    {
        // Arrange
        TrieKey key1 = new byte[] { 0xAA };
        TrieKey key2 = new byte[] { 0xBB };

        // Act
        int prefixLength = key1.CommonPrefixLength(key2);

        // Assert
        prefixLength.Should().Be(0);
    }

    [Test]
    public void Slice_ShouldReturnCorrectSubset_WhenWithinSinglePart()
    {
        // Arrange
        TrieKey key = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        TrieKey slice = key.Slice(1, 3);

        // Assert
        slice.Length.Should().Be(3);
        slice.ToArray().Should().Equal(2, 3, 4);
    }

    [Test]
    public void Slice_ShouldReturnCorrectSubset_WhenSpanningTwoParts()
    {
        // Arrange
        // Constructing a key with two parts by manually using the two-array constructor:
        var part0 = new byte[] { 1, 2, 3 };
        var part1 = new byte[] { 4, 5, 6, 7 };
        TrieKey key = new(part0, part1);

        // Act
        // Span from index 2 (in part0) to index 4 overall
        // That means the slice should include {3, 4, 5}.
        TrieKey slice = key.Slice(2, 3);

        // Assert
        slice.Length.Should().Be(3);
        slice.ToArray().Should().Equal(3, 4, 5);
    }

    [Test]
    public void Slice_ShouldReturnEmpty_WhenLengthIsZero()
    {
        // Arrange
        TrieKey key = new byte[] { 1, 2, 3 };

        // Act
        TrieKey slice = key.Slice(2, 0);

        // Assert
        slice.Should().BeEquivalentTo(TrieKey.Empty);
        slice.Length.Should().Be(0);
    }

    [Test]
    public void Slice_ShouldThrow_WhenInvalidStartOrLength()
    {
        // Arrange
        TrieKey key = new byte[] { 1, 2, 3 };

        // Act
        Action negativeStart = () => key.Slice(-1, 1);
        Action negativeLength = () => key.Slice(0, -1);
        Action outOfRangeSlice = () => key.Slice(1, 10);

        // Assert
        negativeStart.Should().Throw<ArgumentOutOfRangeException>();
        negativeLength.Should().Throw<ArgumentOutOfRangeException>();
        outOfRangeSlice.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void EqualityOperator_ShouldReturnTrue_WhenKeysAreIdentical()
    {
        // Arrange
        TrieKey key1 = new byte[] { 10, 20, 30 };
        TrieKey key2 = new byte[] { 10, 20, 30 };

        // Act & Assert
        (key1 == key2).Should().BeTrue();
        (key1 != key2).Should().BeFalse();
        key1.Equals(key2).Should().BeTrue();
    }

    [Test]
    public void EqualityOperator_ShouldReturnFalse_WhenKeysDiffer()
    {
        // Arrange
        TrieKey key1 = new byte[] { 10, 20, 30 };
        TrieKey key2 = new byte[] { 10, 20, 31 };

        // Act & Assert
        (key1 == key2).Should().BeFalse();
        (key1 != key2).Should().BeTrue();
        key1.Equals(key2).Should().BeFalse();
    }

    [Test]
    public void ToArray_ShouldReturnCombinedBytes()
    {
        // Arrange
        TrieKey key1 = new byte[] { 1, 2 };
        TrieKey key2 = new byte[] { 3, 4 };
        TrieKey combined = new(key1, key2);

        // Act
        byte[] result = combined.ToArray();

        // Assert
        result.Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void GetHashCode_ShouldThrowNotImplementedException_ByDefault()
    {
        // Arrange
        TrieKey key = new byte[] { 1, 2, 3 };

        // Act
        Action act = () => _ = key.GetHashCode();

        // Assert
        act.Should().Throw<NotImplementedException>();
    }

    [Test]
    public void ZeroLengthConcatenation_ShouldYieldOtherKey()
    {
        // Arrange
        TrieKey key1 = TrieKey.Empty;
        TrieKey key2 = new byte[] { 4, 5, 6 };

        // Act
        TrieKey concatenated1 = new(key1, key2);
        TrieKey concatenated2 = new(key2, key1);

        // Assert
        concatenated1.ToArray().Should().Equal(4, 5, 6);
        concatenated2.ToArray().Should().Equal(4, 5, 6);
    }
}
