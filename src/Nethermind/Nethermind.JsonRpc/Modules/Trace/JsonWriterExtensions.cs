// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public static class JsonWriterExtensions
    {
        public static void WriteProperty<T>(this JsonWriter jsonWriter, string propertyName, T propertyValue)
        {
            jsonWriter.WritePropertyName(propertyName);
            jsonWriter.WriteValue(propertyValue);
        }

        public static void WriteProperty<T>(this JsonWriter jsonWriter, string propertyName, T propertyValue, JsonSerializer serializer)
        {
            jsonWriter.WritePropertyName(propertyName);
            serializer.Serialize(jsonWriter, propertyValue);
        }
    }
}
