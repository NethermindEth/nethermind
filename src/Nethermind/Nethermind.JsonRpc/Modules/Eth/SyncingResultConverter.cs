// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Trace;

namespace Nethermind.JsonRpc.Modules.Eth
{
    using Newtonsoft.Json;

    public class SyncingResultConverter : JsonConverter<SyncingResult>
    {
        public override void WriteJson(JsonWriter writer, SyncingResult value, JsonSerializer serializer)
        {
            if (!value.IsSyncing)
            {
                writer.WriteValue(false);
                return;
            }

            writer.WriteStartObject();
            writer.WriteProperty("startingBlock", value.StartingBlock, serializer);
            writer.WriteProperty("currentBlock", value.CurrentBlock, serializer);
            writer.WriteProperty("highestBlock", value.HighestBlock, serializer);
            writer.WriteEndObject();
        }

        public override SyncingResult ReadJson(JsonReader reader, Type objectType, SyncingResult existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
