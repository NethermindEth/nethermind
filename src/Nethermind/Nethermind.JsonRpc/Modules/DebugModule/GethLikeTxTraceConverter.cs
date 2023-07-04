// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
    {
        public override void WriteJson(JsonWriter writer, GethLikeTxTrace value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();

            writer.WriteProperty("gas", value.Gas, serializer);
            writer.WriteProperty("failed", value.Failed);
            writer.WriteProperty("returnValue", value.ReturnValue, serializer);

            writer.WritePropertyName("structLogs");
            WriteEntries(writer, value.Entries, serializer);

            writer.WriteEndObject();
        }

        private static void WriteEntries(JsonWriter writer, List<GethTxTraceEntry> entries, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            foreach (GethTxTraceEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteProperty("pc", entry.Pc);
                writer.WriteProperty("op", entry.Operation);
                writer.WriteProperty("gas", entry.Gas);
                writer.WriteProperty("gasCost", entry.GasCost);
                writer.WriteProperty("depth", entry.Depth);
                writer.WriteProperty("error", entry.Error);
                writer.WritePropertyName("stack");
                writer.WriteStartArray();
                foreach (string stackItem in entry.Stack)
                {
                    writer.WriteValue(stackItem);
                }

                writer.WriteEndArray();

                writer.WritePropertyName("memory");
                writer.WriteStartArray();
                foreach (string memory in entry.Memory)
                {
                    writer.WriteValue(memory);
                }
                writer.WriteEndArray();

                writer.WritePropertyName("storage");
                writer.WriteStartObject();
                foreach ((string storageIndex, string storageValue) in entry.SortedStorage)
                {
                    writer.WriteProperty(storageIndex, storageValue);
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        public override GethLikeTxTrace ReadJson(JsonReader reader, Type objectType, GethLikeTxTrace existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
