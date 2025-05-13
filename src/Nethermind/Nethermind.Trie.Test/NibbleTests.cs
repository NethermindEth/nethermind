// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NibbleTests
{

    public static IEnumerable<TestCaseData> GetTestCases()
    {
        yield return new(new byte[] { 0x00 }, new byte[] { });
        yield return new(new byte[] { 0x20 }, new byte[] { 16 });
        yield return new(new byte[] { 0x11, 0x23, 0x45 }, new byte[] { 1, 2, 3, 4, 5 });
        yield return new(new byte[] { 0x00, 0x01, 0x23, 0x45 }, new byte[] { 0, 1, 2, 3, 4, 5 });
        yield return new(new byte[] { 0x3f, 0x1c, 0xb8 }, new byte[] { 15, 1, 12, 11, 8, 16 });
        yield return new(new byte[] { 0x20, 0x0f, 0x1c, 0xb8 }, new byte[] { 0, 15, 1, 12, 11, 8, 16 });
    }

    [TestCaseSource(nameof(GetTestCases))]
    public void CompactDecodingTest(byte[] compact, byte[] hex)
    {
        Nibbles.CompactToHexEncode(compact).Should().BeEquivalentTo(hex);
    }
}
