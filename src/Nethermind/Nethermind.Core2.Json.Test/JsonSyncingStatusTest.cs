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

using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core2.Api;
using NUnit.Framework;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.Core2.Json.Test
{
    [TestFixture]
    public class JsonSyncingStatusTest
    {
        [Test]
        public async Task SyncingStatus_Serialize()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            SyncingStatus syncingStatus = new SyncingStatus(new Slot(1), new Slot(2), new Slot(3));

            // Act - serialize to string
            await using MemoryStream memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(memoryStream, syncingStatus, options);
            string jsonString = Encoding.UTF8.GetString(memoryStream.ToArray());
            
            // Assert
            jsonString.ShouldBe("{\"current_slot\":2,\"highest_slot\":3,\"starting_slot\":1}");
        }
        
        [Test]
        public async Task SyncingStatus_Deserialize()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            string jsonString = "{\"current_slot\":2,\"highest_slot\":3,\"starting_slot\":1}";

            // Act - deserialize from string
            await using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            SyncingStatus syncingStatus = await JsonSerializer.DeserializeAsync<SyncingStatus>(memoryStream, options);
            
            syncingStatus.StartingSlot.ShouldBe(Slot.One);
            syncingStatus.CurrentSlot.ShouldBe(new Slot(2));
            syncingStatus.HighestSlot.ShouldBe(new Slot(3));
        }
        
        [Test]
        public async Task SyncingStatus_DeserializeAlternativeOrder()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            string jsonString = "{\"starting_slot\":1,\"current_slot\":2,\"highest_slot\":3}";

            // Act - deserialize from string
            await using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            SyncingStatus syncingStatus = await JsonSerializer.DeserializeAsync<SyncingStatus>(memoryStream, options);
            
            syncingStatus.StartingSlot.ShouldBe(Slot.One);
            syncingStatus.CurrentSlot.ShouldBe(new Slot(2));
            syncingStatus.HighestSlot.ShouldBe(new Slot(3));
        }
    }
}