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
    public class SszBeaconBlockTests
    {
        [Test]
        public void EmptyBeaconBlockEncode()
        {
            // Arrange
            Eth1Data eth1Data = new Eth1Data(
                new Root(Enumerable.Repeat((byte) 0x12, 32).ToArray()),
                64,
                new Bytes32(Enumerable.Repeat((byte) 0x34, 32).ToArray()));
            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                new BlsSignature(Enumerable.Repeat((byte) 0x56, 96).ToArray()),
                eth1Data,
                new Bytes32(Enumerable.Repeat((byte) 0x78, 32).ToArray()),
                new ProposerSlashing[0],
                new AttesterSlashing [0], 
                new Attestation[0],
                new Deposit[0],
                new SignedVoluntaryExit[0]
            );
            BeaconBlock beaconBlock = new BeaconBlock(
                Slot.One,
                new Root(Enumerable.Repeat((byte) 0x9a, 32).ToArray()),
                new Root(Enumerable.Repeat((byte) 0xbc, 32).ToArray()),
                beaconBlockBody
                );
            
            // Act
            Span<byte> encoded = new byte[Ssz.BeaconBlockLength(beaconBlock)];
            Ssz.Encode(encoded, beaconBlock);
            
            // Assert
            string expectedHex =
                // static
                "0100000000000000" +
                "9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a" +
                "bcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbc" +
                "4c000000" + // body dynamic offset 8+32+32+4 = 76 = 0x4c
                // dynamic
                // - body - static
                "565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656" +
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "4000000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434" +
                "7878787878787878787878787878787878787878787878787878787878787878" +
                "dc000000" + // proposer dynamic offset 96+(32+8+32)+32 + 5*4 = 220 = 0xdc
                "dc000000" + // attester slashings & all remaining offsets are the same, as they are empty
                "dc000000" +
                "dc000000" +
                "dc000000"; // body - dynamic part is empty
                
            encoded.ToHexString().ShouldBe(expectedHex);
        }
        
        [Test]
        public void EmptyBeaconBlockDecode()
        {
            // Arrange
            string hex =
                // static
                "0100000000000000" +
                "9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a9a" +
                "bcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbcbc" +
                "4c000000" + // body dynamic offset 8+32+32+4 = 76 = 0x4c
                // dynamic
                // - body - static
                "565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656" +
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "4000000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434" +
                "7878787878787878787878787878787878787878787878787878787878787878" +
                "dc000000" + // proposer dynamic offset 96+(32+8+32)+32 + 5*4 = 220 = 0xdc
                "dc000000" + // attester slashings & all remaining offsets are the same, as they are empty
                "dc000000" +
                "dc000000" +
                "dc000000"; // body - dynamic part is empty
            
            byte[] bytes = Bytes.FromHexString(hex);
            
            // Act
            BeaconBlock beaconBlock = Ssz.DecodeBeaconBlock(bytes);
            
            // Assert
            beaconBlock.Slot.ShouldBe(Slot.One);
            beaconBlock.ParentRoot.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x9a, 32).ToArray());
            beaconBlock.StateRoot.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0xbc, 32).ToArray());
            beaconBlock.Body.RandaoReveal.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x56, 96).ToArray());
            beaconBlock.Body.Eth1Data.DepositRoot.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x12, 32).ToArray());
            beaconBlock.Body.Eth1Data.DepositCount.ShouldBe(64uL);
            beaconBlock.Body.Eth1Data.BlockHash.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x34, 32).ToArray());
            beaconBlock.Body.Graffiti.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x78, 32).ToArray());
            beaconBlock.Body.ProposerSlashings.Count.ShouldBe(0);
            beaconBlock.Body.AttesterSlashings.Count.ShouldBe(0);
            beaconBlock.Body.Attestations.Count.ShouldBe(0);
            beaconBlock.Body.Deposits.Count.ShouldBe(0);
            beaconBlock.Body.VoluntaryExits.Count.ShouldBe(0);
        }
    }
}