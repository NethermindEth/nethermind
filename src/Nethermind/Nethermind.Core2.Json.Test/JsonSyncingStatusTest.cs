// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
