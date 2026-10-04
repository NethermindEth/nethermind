// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>Serializes a log list, writing a receipt's own logs straight from the stored entries.</summary>
internal sealed class LogsForRpcConverter : JsonConverter<IReadOnlyList<LogEntryForRpc>>
{
    public override IReadOnlyList<LogEntryForRpc>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TypeInfoJsonSerializer.Deserialize<LogEntryForRpc[]>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<LogEntryForRpc> value, JsonSerializerOptions options)
    {
        if (value is ReceiptLogsForRpc receiptLogs)
        {
            receiptLogs.Write(writer, options);
            return;
        }

        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
        {
            TypeInfoJsonSerializer.Serialize(writer, value[i], options);
        }

        writer.WriteEndArray();
    }
}
