// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
public class TrieKeyMultiPartTests
{
    // For brevity, some tests may re-verify functionality that also
    // appears in simpler single-part tests. Here we specifically
    // focus on multi-part cases and edge scenarios.

    [Test]
    public void CreateTwoPartKey_WithDifferentSizedArrays_ShouldStoreAllBytes()
    {
        // part0 is length 2, part1 is length 5
        byte[] part0 = [0x01, 0x02];
        byte[] part1 = [0x03, 0x04, 0x05, 0x06, 0x07];

        // Act
        TrieKey multiPartKey = new TrieKey(part0, part1);

        // Assert
        multiPartKey.Length.Should().Be(7);
        multiPartKey.ToArray().Should().Equal(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
    }

    [Test]
    public void Indexer_ShouldCorrectlyAddressBytesInMultiPartKey()
    {
        // part0 is length 3, part1 is length 3
        byte[] part0 = [10, 11, 12];
        byte[] part1 = [20, 21, 22];
        TrieKey multiPartKey = new TrieKey(part0, part1);

        // Act & Assert
        multiPartKey[0].Should().Be(10);
        multiPartKey[1].Should().Be(11);
        multiPartKey[2].Should().Be(12);
        multiPartKey[3].Should().Be(20);
        multiPartKey[4].Should().Be(21);
        multiPartKey[5].Should().Be(22);

        // Confirm the total length
        multiPartKey.Length.Should().Be(6);
    }

    [Test]
    public void Slice_WithStartAndLengthWithinFirstPartOnly_ShouldReturnCorrectKey()
    {
        // part0 is length 4, part1 is length 2
        byte[] part0 = [1, 2, 3, 4];
        byte[] part1 = [5, 6];
        TrieKey multiPartKey = new(part0, part1);

        // Slice wholly in part0 => from index 1 to index 3 => bytes {2,3,4}
        TrieKey sliced = multiPartKey.Slice(1, 3);

        sliced.Length.Should().Be(3);
        sliced.ToArray().Should().Equal(2, 3, 4);
    }

    [Test]
    public void Slice_WithStartAndLengthWithinSecondPartOnly_ShouldReturnCorrectKey()
    {
        // part0 is length 2, part1 is length 4
        byte[] part0 = [10, 11];
        byte[] part1 = [20, 21, 22, 23];
        TrieKey multiPartKey = new(part0, part1);

        // Slice wholly in part1 => start at index=2 (where part1 begins),
        // length=3 => bytes {20,21,22}
        TrieKey sliced = multiPartKey.Slice(2, 3);

        sliced.Length.Should().Be(3);
        sliced.ToArray().Should().Equal(20, 21, 22);
    }

    [Test]
    public void Slice_SpanningBothParts_ShouldReturnCorrectKey()
    {
        // part0 is length 3, part1 is length 3
        byte[] part0 = [0xAA, 0xBB, 0xCC];
        byte[] part1 = [0xDD, 0xEE, 0xFF];
        TrieKey multiPartKey = new(part0, part1);

        // Slice from index=1 in part0 -> 4 bytes total => {0xBB,0xCC,0xDD,0xEE}
        TrieKey sliced = multiPartKey.Slice(1, 4);

        sliced.Length.Should().Be(4);
        sliced.ToArray().Should().Equal(0xBB, 0xCC, 0xDD, 0xEE);
    }

    [Test]
    public void CommonPrefixLength_WithKeysSpanningParts_ShouldHandlePartialOverlap()
    {
        // Arrange
        // multiPartKey1 => part0 {1,2}, part1 {3,4,5}
        byte[] key1Part0 = [1, 2];
        byte[] key1Part1 = [3, 4, 5];
        TrieKey multiPartKey1 = new(key1Part0, key1Part1);

        // multiPartKey2 => part0 {1}, part1 {2,3,9}
        byte[] key2Part0 = [1];
        byte[] key2Part1 = [2, 3, 9];
        TrieKey multiPartKey2 = new(key2Part0, key2Part1);

        // Act
        // Both keys are {1,2,3,...} for the first three bytes, except the second key has a '9' after that
        // so the prefix should be 3.
        int prefixLength = multiPartKey1.CommonPrefixLength(multiPartKey2);

        // Assert
        prefixLength.Should().Be(3);
    }

    [Test]
    public void Equality_WithSameBytesButDifferentPartSplits_ShouldBeEqual()
    {
        // part0 is length 2, part1 is length 2
        // effectively the combined array is {1,2,3,4}
        byte[] keyA1 = [1, 2];
        byte[] keyA2 = [3, 4];
        TrieKey multiPartKeyA = new(keyA1, keyA2);

        // Single part array is exactly {1,2,3,4}
        byte[] singleArray = [1, 2, 3, 4];
        TrieKey singlePartKey = new(singleArray);

        // part0 is length 3, part1 is length 1 => effectively also {1,2,3,4}
        byte[] keyB1 = [1, 2, 3];
        byte[] keyB2 = [4];
        TrieKey multiPartKeyB = new(keyB1, keyB2);

        // Act & Assert
        multiPartKeyA.Should().Be(singlePartKey);
        multiPartKeyA.Should().Be(multiPartKeyB);
        singlePartKey.Should().Be(multiPartKeyB);

        (multiPartKeyA == singlePartKey).Should().BeTrue();
        (multiPartKeyA == multiPartKeyB).Should().BeTrue();
        (singlePartKey == multiPartKeyB).Should().BeTrue();
    }

    [Test]
    public void Equality_WithDifferentBytesAndDifferentPartSplits_ShouldBeFalse()
    {
        // keyX => {1,2,3,4}
        byte[] xPart0 = [1, 2];
        byte[] xPart1 = [3, 4];
        TrieKey keyX = new(xPart0, xPart1);

        // keyY => {1,2,5,4} (different in the third byte)
        byte[] yPart0 = [1, 2];
        byte[] yPart1 = [5, 4];
        TrieKey keyY = new(yPart0, yPart1);

        // Act & Assert
        keyX.Should().NotBe(keyY);
        (keyX == keyY).Should().BeFalse();
    }

    [Test]
    public void PrependByte_WithMultiPartKey_ShouldProduceCorrectResult()
    {
        // baseKey => {2,3,4,5}, stored in two parts
        byte[] basePart0 = [2, 3];
        byte[] basePart1 = [4, 5];
        TrieKey baseKey = new(basePart0, basePart1);

        // Prepend byte = 1 => new key => {1,2,3,4,5}
        TrieKey prepended = new(0x01, baseKey);

        prepended.Length.Should().Be(5);
        prepended.ToArray().Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public void TwoPartConcatenation_WithOtherTwoPartKey_ShouldCombineAllBytes()
    {
        // Key1 => part0: {1,2}, part1: {3}
        TrieKey key1 = new([1, 2], [3]);

        // Key2 => part0: {4}, part1: {5,6}
        TrieKey key2 = new([4], [5, 6]);

        // Act
        TrieKey concatenated = new(key1, key2);

        // combined => {1,2,3,4,5,6}
        concatenated.Length.Should().Be(6);
        concatenated.ToArray().Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Test]
    public void Slice_WhenSpanningPart0AndPart1Entirely_ShouldReturnTheWholeKey()
    {
        // Key => part0: {10,20}, part1: {30,40}
        TrieKey key = new([10, 20], [30, 40]);
        // total length => 4

        // Act - slicing from 0 to the entire length
        TrieKey entireSlice = key.Slice(0, 4);

        // Assert
        entireSlice.Length.Should().Be(4);
        entireSlice.ToArray().Should().Equal(10, 20, 30, 40);
        entireSlice.Should().Be(key);
    }

    [Test]
    public void Slice_WhenItExactlyCutsAfterPart0_ShouldReturnPart1()
    {
        // Key => part0: {1,2,3}, part1: {4,5}
        TrieKey key = new([1, 2, 3], [4, 5]);
        // length => 5

        // Act - slice that starts where part1 begins => index 3, length 2 => {4,5}
        TrieKey slice = key.Slice(3, 2);

        // Assert
        slice.Length.Should().Be(2);
        slice.ToArray().Should().Equal(4, 5);
    }

    [Test]
    public void CommonPrefixLength_WithKeysWhollyInPart1_ShouldCompareCorrectly()
    {
        // Key1 => part0:{}, part1:{10,20,30}
        TrieKey key1 = new(Array.Empty<byte>(), [10, 20, 30]);

        // Key2 => part0:{10}, part1:{20,99}
        // effectively => {10,20,99}
        TrieKey key2 = new([10], [20, 99]);

        // First key => [10,20,30]
        // Second key => [10,20,99]
        // They match on the first two bytes => prefix = 2
        int prefixLength = key1.CommonPrefixLength(key2);

        prefixLength.Should().Be(2);
    }

    [Test]
    public void ToArray_ShouldCorrectlyCombineMultiPartKeyWithDifferingLengths()
    {
        // part0 is length 5, part1 is length 1
        byte[] part0 = [0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
        byte[] part1 = [0xFF];
        TrieKey key = new(part0, part1);

        byte[] array = key.ToArray();
        array.Should().Equal(0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xFF);
    }
}
