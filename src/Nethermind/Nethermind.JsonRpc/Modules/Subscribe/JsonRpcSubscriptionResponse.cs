// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class JsonRpcSubscriptionResponse : JsonRpcResponse
    {
        [JsonPropertyName("method")]
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string MethodName { get; set; }

        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcSubscriptionResult Params { get; set; }

        [JsonPropertyOrder(3)]
        [JsonConverter(typeof(JsonRpcIdConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public new JsonRpcId Id { get { return base.Id; } set { base.Id = value; } }

        internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
        {
            JsonRpcResponseWriter.WriteEnvelopeStart(writer);

            if (MethodName is not null)
            {
                writer.WriteString("method"u8, MethodName);
            }

            if (Params is not null)
            {
                writer.WritePropertyName("params"u8);
                JsonSerializer.Serialize(writer, Params, RpcPayloadTypeInfo<JsonRpcSubscriptionResult>.Get(options));
            }

            if (!Id.IsMissing)
            {
                writer.WritePropertyName("id"u8);
                Id.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
    }
}
