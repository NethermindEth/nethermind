// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NibbleTests
{
    private readonly byte[][] _hexEncoding =
    [
        [],
        [16],
        [1, 2, 3, 4, 5],
        [0, 1, 2, 3, 4, 5],
        [15, 1, 12, 11, 8, 16],
        [0, 15, 1, 12, 11, 8, 16]
    ];

    private readonly byte[][] _compactEncoding =
    [
        [0x00],
        [0x20],
        [0x11, 0x23, 0x45],
        [0x00, 0x01, 0x23, 0x45],
        [0x3f, 0x1c, 0xb8],
        [0x20, 0x0f, 0x1c, 0xb8]
    ];

    [Test]
    public void CompactDecodingTest()
    {
        for (int i = 0; i < _compactEncoding.Length; i++)
        {
            byte[]? encoded = _compactEncoding[i];
            Nibbles.CompactToHexEncode(encoded).Should().BeEquivalentTo(_hexEncoding[i]);
        }
    }


}
