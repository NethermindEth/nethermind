// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
{
    public override GethLikeTxTrace ReadJson(
        JsonReader reader,
        Type objectType,
        GethLikeTxTrace existingValue,
        bool hasExistingValue,
        JsonSerializer serializer) => throw new NotSupportedException();

    public override void WriteJson(JsonWriter writer, GethLikeTxTrace value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else if (value.CustomTracerResult is not null)
        {
            serializer.Serialize(writer, value.CustomTracerResult);
        }
        else
        {
            writer.WriteStartObject();

            writer.WriteProperty("gas", value.Gas, serializer);
            writer.WriteProperty("failed", value.Failed);
            writer.WriteProperty("returnValue", value.ReturnValue, serializer);

            writer.WritePropertyName("structLogs");
            WriteEntries(writer, value.Entries, serializer);

            writer.WriteEndObject();
        }
    }

    private static void WriteEntries(JsonWriter writer, List<GethTxTraceEntry> entries, JsonSerializer _)
    {
        writer.WriteStartArray();
        foreach (GethTxTraceEntry entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteProperty("pc", entry.ProgramCounter);
            writer.WriteProperty("op", entry.Opcode);
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

            if (entry.Memory is not null)
            {
                writer.WritePropertyName("memory");
                writer.WriteStartArray();
                foreach (string memory in entry.Memory)
                {
                    writer.WriteValue(memory);
                }
                writer.WriteEndArray();
            }

            if (entry.Storage is not null)
            {
                writer.WritePropertyName("storage");
                writer.WriteStartObject();

                foreach (var item in entry.Storage.OrderBy(s => s.Key))
                {
                    writer.WriteProperty(item.Key, item.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
