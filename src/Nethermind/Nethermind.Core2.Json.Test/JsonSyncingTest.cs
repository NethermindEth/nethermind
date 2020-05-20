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
    public class JsonSyncingTest
    {
        [Test]
        public async Task Syncing_SerializeNullStatus()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            Syncing syncing = new Syncing(true, null);

            // Act - serialize to string
            await using MemoryStream memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(memoryStream, syncing, options);
            string jsonString = Encoding.UTF8.GetString(memoryStream.ToArray());
            
            // Assert
            jsonString.ShouldBe("{\"is_syncing\":true,\"sync_status\":null}");
        }

        [Test]
        public async Task Syncing_SerializeWithStatus()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            Syncing syncing = new Syncing(true, new SyncingStatus(Slot.One, new Slot(2), new Slot(3)));

            // Act - serialize to string
            await using MemoryStream memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(memoryStream, syncing, options);
            string jsonString = Encoding.UTF8.GetString(memoryStream.ToArray());
            
            // Assert
            jsonString.ShouldBe("{\"is_syncing\":true,\"sync_status\":{\"current_slot\":2,\"highest_slot\":3,\"starting_slot\":1}}");
        }

        [Test]
        public async Task Syncing_DeserializeAlternativeOrderNullStatus()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            string jsonString = "{\"sync_status\":null,\"is_syncing\":true}";

            // Act - deserialize from string
            await using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            Syncing syncing = await JsonSerializer.DeserializeAsync<Syncing>(memoryStream, options);
            
            syncing.IsSyncing.ShouldBeTrue();
            syncing.SyncStatus.ShouldBeNull();
        }
        
        [Test]
        public async Task Syncing_DeserializeWithStatus()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();
            string jsonString = "{\"is_syncing\":true,\"sync_status\":{\"current_slot\":2,\"highest_slot\":3,\"starting_slot\":1}}";

            // Act - deserialize from string
            await using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            Syncing syncing = await JsonSerializer.DeserializeAsync<Syncing>(memoryStream, options);
            
            syncing.IsSyncing.ShouldBeTrue();
            syncing.SyncStatus!.CurrentSlot.ShouldBe(new Slot(2));
        }
    }
}