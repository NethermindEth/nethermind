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
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Core2.Types;
using NUnit.Framework;
using Shouldly;
using Bytes = Nethermind.Core2.Bytes;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class SszAttestationTests
    {
        [TestCase]
        public void BasicAttestationEncode()
        {
            // Arrange
            Attestation attestation = new Attestation(
                new BitArray(new[] {
                    true, false, false, false, false, true, false, false, 
                    true, true, false}),
                new AttestationData(
                    new Slot(2 * 8 + 5),
                    new CommitteeIndex(2),
                    new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()),
                    new Checkpoint(
                        new Epoch(1),
                        new Root(Enumerable.Repeat((byte)0x34, 32).ToArray())
                    ), 
                    new Checkpoint(
                        new Epoch(2),
                        new Root(Enumerable.Repeat((byte)0x56, 32).ToArray())
                    )
                ),
                new BlsSignature(Enumerable.Repeat((byte) 0xef, 96).ToArray()));
            
            // Act
            Span<byte> encoded = new byte[Ssz.AttestationLength(attestation)];
            Ssz.Encode(encoded, attestation);
            
            // Assert
            
            // Bitlist is little endian
            // true, false, false, false, false, true, false, false, = 0b 0010 0001 = 0x 21
            // true, true, false}), = 0b 0000 0011, add sentinel bit (so we know only 3 bits used) -> 0b 0000 1011 = 0x 0b
            
            string expectedHex =
                // static
                "e4000000" + // aggregation dynamic offset 4 + 8+8+32+(8+32)+(8+32) + 96 = 228 = 0xe4
                "1500000000000000" +
                "0200000000000000" +
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "0100000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434" +
                "0200000000000000" +
                "5656565656565656565656565656565656565656565656565656565656565656" +
                "efefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef" +
                // dynamic part
                "210b";
            
            encoded.ToHexString().ShouldBe(expectedHex);
        }

        [TestCase]
        public void BasicEth1DataDecode()
        {
            // Arrange
            string hex =
                // static
                "e4000000" + // aggregation dynamic offset 4 + 8+8+32+(8+32)+(8+32) + 96 = 228 = 0xe5
                "1500000000000000" +
                "0200000000000000" +
                "1212121212121212121212121212121212121212121212121212121212121212" +
                "0100000000000000" +
                "3434343434343434343434343434343434343434343434343434343434343434" +
                "0200000000000000" +
                "5656565656565656565656565656565656565656565656565656565656565656" +
                "efefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef" +
                // dynamic part
                "210b";
            byte[] bytes = Bytes.FromHexString(hex);
            
            // Act
            Attestation attestation = Ssz.DecodeAttestation(bytes);
            
            // Assert
            attestation.Data.Slot.ShouldBe(new Slot(21));
            attestation.Data.Target.Epoch.ShouldBe(new Epoch(2));
            attestation.Data.Target.Root.AsSpan()[31].ShouldBe((byte)0x56);
            attestation.Signature.Bytes[95].ShouldBe((byte)0xef);

            attestation.AggregationBits[0].ShouldBeTrue();
            attestation.AggregationBits[5].ShouldBeTrue();
            attestation.AggregationBits.Length.ShouldBe(11);
        }
    }
}