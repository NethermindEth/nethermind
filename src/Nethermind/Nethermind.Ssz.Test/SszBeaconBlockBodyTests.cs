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
    public class SszBeaconBlockBodyTests
    {
        [TestCase]
        public void EmptyBeaconBlockBodyEncode()
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
            
            // Act
            Span<byte> encoded = new byte[Ssz.BeaconBlockBodyLength(beaconBlockBody)];
            Ssz.Encode(encoded, beaconBlockBody);
            
            // Assert
            string expectedHex =
                // static
                "565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656" +
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "4000000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434" +
                "7878787878787878787878787878787878787878787878787878787878787878" +
                "dc000000" + // proposer dynamic offset 96+(32+8+32)+32 + 5*4 = 220 = 0xdc
                "dc000000" + // attester slashings & all remaining offsets are the same, as they are empty
                "dc000000" +
                "dc000000" +
                "dc000000"; // dynamic part is empty
                
            encoded.ToHexString().ShouldBe(expectedHex);
        }
        
        [TestCase]
        public void EmptyBeaconBlockBodyDecode()
        {
            // Arrange
            string hex =
                // static
                "565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656" +
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "4000000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434" +
                "7878787878787878787878787878787878787878787878787878787878787878" +
                "dc000000" + // proposer dynamic offset 96+(32+8+32)+32 + 5*4 = 220 = 0xdc
                "dc000000" + // attester slashings & all remaining offsets are the same, as they are empty
                "dc000000" +
                "dc000000" +
                "dc000000"; // dynamic part is empty
            
            byte[] bytes = Bytes.FromHexString(hex);
            
            // Act
            BeaconBlockBody beaconBlockBody = Ssz.DecodeBeaconBlockBody(bytes);
            
            // Assert
            beaconBlockBody.RandaoReveal.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x56, 96).ToArray());
            beaconBlockBody.Eth1Data.DepositRoot.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x12, 32).ToArray());
            beaconBlockBody.Eth1Data.DepositCount.ShouldBe(64uL);
            beaconBlockBody.Eth1Data.BlockHash.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x34, 32).ToArray());
            beaconBlockBody.Graffiti.AsSpan().ToArray().ShouldBe(Enumerable.Repeat((byte) 0x78, 32).ToArray());
            beaconBlockBody.ProposerSlashings.Count.ShouldBe(0);
            beaconBlockBody.AttesterSlashings.Count.ShouldBe(0);
            beaconBlockBody.Attestations.Count.ShouldBe(0);
            beaconBlockBody.Deposits.Count.ShouldBe(0);
            beaconBlockBody.VoluntaryExits.Count.ShouldBe(0);
        }
    }
}