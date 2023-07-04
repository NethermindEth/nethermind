// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class ZeroPaddedSpanTests
    {
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 0, 1, PadDirection.Right, "0x00")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 1, 1, PadDirection.Right, "0x01")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 1, 15, PadDirection.Right, "0x0102030405060708090a0b0c0d0e0f")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 0, 15, PadDirection.Right, "0x000102030405060708090a0b0c0d0e")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 1, 16, PadDirection.Right, "0x0102030405060708090a0b0c0d0e0f00")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 0, 16, PadDirection.Right, "0x000102030405060708090a0b0c0d0e0f")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 0, 17, PadDirection.Right, "0x000102030405060708090a0b0c0d0e0f00")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 1, 17, PadDirection.Right, "0x0102030405060708090a0b0c0d0e0f0000")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 17, 2, PadDirection.Right, "0x0000")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 16, 2, PadDirection.Right, "0x0000")]
        [TestCase("0x0001", 0, 4, PadDirection.Right, "0x00010000")]
        [TestCase("0x0001", 0, 4, PadDirection.Left, "0x00000001")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 1, 1, PadDirection.Left, "0x01")]
        [TestCase("0x000102030405060708090a0b0c0d0e0f", 0, 17, PadDirection.Left, "0x00000102030405060708090a0b0c0d0e0f")]

        public void Can_slice_with_zero_padding(string inputHex, int startIndex, int length, PadDirection padDirection, string expectedResultHex)
        {
            byte[] input = Bytes.FromHexString(inputHex);
            ZeroPaddedSpan result = input.SliceWithZeroPadding(startIndex, length, padDirection);
            Assert.That(result.ToArray().ToHexString(true), Is.EqualTo(expectedResultHex));
        }
    }
}
