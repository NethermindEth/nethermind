// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Facade.Eth.RpcTransaction;

[JsonConverter(typeof(JsonConverterImpl))]
public interface IRpcTransaction
{
    public static readonly JsonConverter<IRpcTransaction> JsonConverter = new JsonConverterImpl();

    private class JsonConverterImpl : JsonConverter<IRpcTransaction>
    {
        public override IRpcTransaction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IRpcTransaction value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
