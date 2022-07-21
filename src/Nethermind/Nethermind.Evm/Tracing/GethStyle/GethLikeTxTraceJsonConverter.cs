//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle;

internal class GethLikeTxTraceJsonConverter : JsonConverter<GethTxFileTraceEntry>
{
    public override GethTxFileTraceEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, GethTxFileTraceEntry value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        writer.WritePropertyName("pc");
        writer.WriteNumberValue(value.ProgramCounter);

        writer.WritePropertyName("op");
        writer.WriteNumberValue((byte)value.OpcodeRaw);

        writer.WritePropertyName("gas");
        writer.WriteStringValue($"0x{value.Gas:x}");

        writer.WritePropertyName("gasCost");
        writer.WriteStringValue($"0x{value.GasCost:x}");

        if ((value.Memory?.Count ?? 0) > 0)
        {
            var memory = string.Concat(value.Memory);

            writer.WritePropertyName("memory");
            writer.WriteStringValue($"0x{memory}");

            writer.WritePropertyName("memSize");
            writer.WriteNumberValue(memory.Length / 2);
        }

        if (value.Stack != null)
        {
            writer.WritePropertyName("stack");
            writer.WriteStartArray();

            foreach (var s in value.Stack)
                writer.WriteStringValue(s);

            writer.WriteEndArray();
        }

        writer.WritePropertyName("depth");
        writer.WriteNumberValue(value.Depth);

        writer.WritePropertyName("refund");
        writer.WriteNumberValue(value.Refund ?? 0);

        writer.WritePropertyName("opName");
        writer.WriteStringValue(value.Opcode);

        if (value.Error != null)
        {
            writer.WritePropertyName("error");
            writer.WriteStringValue(value.Error);
        }

        writer.WriteEndObject();
    }
}
