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
