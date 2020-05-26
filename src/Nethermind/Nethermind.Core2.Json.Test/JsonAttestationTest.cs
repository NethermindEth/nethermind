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
// 

using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.Core2.Json.Test
{
    [TestFixture]
    public class JsonAttestationTest
    {
        [Test]
        public async Task Attestation_Serialize()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
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

            // Act - serialize to string
            await using MemoryStream memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(memoryStream, attestation, options);
            string jsonString = Encoding.UTF8.GetString(memoryStream.ToArray());
            
            // Assert
            
            // Spec 0.10.1 has json to hex values, of format byte
            // If converted bits to bytes, e.g. little ending, then would not communicate length.
            // Convert bit 0 => 0x00, 1 => 0x01, and byte sequence of 0x00 and 0x01.
            
            jsonString.ShouldBe("{\"aggregation_bits\":\"0x0100000000010000010100\",\"data\":{" +
                                "\"beacon_block_root\":\"0x1212121212121212121212121212121212121212121212121212121212121212\",\"index\":2,\"slot\":21," +
                                "\"source\":{\"epoch\":1,\"root\":\"0x3434343434343434343434343434343434343434343434343434343434343434\"}," +
                                "\"target\":{\"epoch\":2,\"root\":\"0x5656565656565656565656565656565656565656565656565656565656565656\"}" +
                                "},\"signature\":\"0xefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef\"}");
        }
        
        [Test]
        public async Task Attestation_Deserialize()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            string jsonString = "{\"aggregation_bits\":\"0x0100000000010000010100\",\"data\":{" +
                                "\"beacon_block_root\":\"0x1212121212121212121212121212121212121212121212121212121212121212\",\"index\":2,\"slot\":21," +
                                "\"source\":{\"epoch\":1,\"root\":\"0x3434343434343434343434343434343434343434343434343434343434343434\"}," +
                                "\"target\":{\"epoch\":2,\"root\":\"0x5656565656565656565656565656565656565656565656565656565656565656\"}" +
                                "},\"signature\":\"0xefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef\"}";

            // Act - deserialize from string
            await using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            Attestation attestation = await JsonSerializer.DeserializeAsync<Attestation>(memoryStream, options);
            
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
