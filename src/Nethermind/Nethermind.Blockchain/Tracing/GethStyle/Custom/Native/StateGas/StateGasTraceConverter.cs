// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;

public class StateGasTraceConverter : JsonConverter<StateGasTrace>
{
    public override void Write(Utf8JsonWriter writer, StateGasTrace value, JsonSerializerOptions options)
    {
        // The enclosing custom-trace converter serializes numbers as raw decimals; the execution-apis
        // schema requires hex quantities, so force hex for the duration of this object.
        NumberConversion previousValue = ForcedNumberConversion.Value;
        try
        {
            ForcedNumberConversion.Value = NumberConversion.Hex;

            writer.WriteStartObject();
            writer.WritePropertyName("gasUsed"u8);
            JsonSerializer.Serialize(writer, value.GasUsed, options);
            writer.WritePropertyName("regularGasUsed"u8);
            JsonSerializer.Serialize(writer, value.RegularGasUsed, options);
            writer.WritePropertyName("stateGasUsed"u8);
            JsonSerializer.Serialize(writer, value.StateGasUsed, options);
            writer.WritePropertyName("gasRefund"u8);
            JsonSerializer.Serialize(writer, value.GasRefund, options);
            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }
    }

    public override StateGasTrace Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
