// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NibbleTests
{
    private readonly byte[][] _hexEncoding = {
        new byte[]{},
        new byte[]{16},
        new byte[]{1, 2, 3, 4, 5},
        new byte[]{0, 1, 2, 3, 4, 5},
        new byte[]{15, 1, 12, 11, 8, 16},
        new byte[]{0, 15, 1, 12, 11, 8, 16},

    };

    private readonly byte[][] _compactEncoding = {
        new byte[]{0x00},
        new byte[]{0x20},
        new byte[]{0x11, 0x23, 0x45},
        new byte[]{0x00, 0x01, 0x23, 0x45},
        new byte[]{0x3f, 0x1c, 0xb8},
        new byte[]{0x20, 0x0f, 0x1c, 0xb8},
    };

    [Test]
    public void CompactDecodingTest()
    {
        for (int i = 0; i < _compactEncoding.Length; i++)
        {
            Nibbles.CompactToHexEncode(_compactEncoding[i]).Should().BeEquivalentTo(_hexEncoding[i]);
        }
    }


}
