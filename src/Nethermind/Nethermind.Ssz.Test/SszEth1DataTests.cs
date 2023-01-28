// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NUnit.Framework;
using Shouldly;
using Bytes = Nethermind.Core2.Bytes;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class SszEth1DataTests
    {
        [TestCase]
        public void BasicEth1DataEncode()
        {
            // Arrange
            Eth1Data eth1Data = new Eth1Data(
                new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()),
                64,
                new Bytes32(Enumerable.Repeat((byte)0x34, 32).ToArray()));

            // Act
            Span<byte> encoded = new byte[Ssz.Eth1DataLength];
            Ssz.Encode(encoded, eth1Data);

            // Assert
            string expectedHex =
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "4000000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434";
            encoded.ToHexString().ShouldBe(expectedHex);
        }

        [TestCase]
        public void BasicEth1DataDecode()
        {
            // Arrange
            string hex =
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "4000000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434";
            byte[] bytes = Bytes.FromHexString(hex);

            // Act
            Eth1Data eth1Data = Ssz.DecodeEth1Data(bytes);

            // Assert
            eth1Data.DepositRoot.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte)0x12, 32).ToArray());
            eth1Data.DepositCount.ShouldBe(64uL);
            eth1Data.BlockHash.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte)0x34, 32).ToArray());
        }
    }
}
