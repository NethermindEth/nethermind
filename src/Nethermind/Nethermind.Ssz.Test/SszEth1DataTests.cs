//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
                new Root(Enumerable.Repeat((byte) 0x12, 32).ToArray()),
                64,
                new Bytes32(Enumerable.Repeat((byte) 0x34, 32).ToArray()));
            
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
            eth1Data.DepositRoot.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x12, 32).ToArray());
            eth1Data.DepositCount.ShouldBe(64uL);
            eth1Data.BlockHash.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x34, 32).ToArray());
        }
    }
}