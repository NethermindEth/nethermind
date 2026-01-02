// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using FastEnumUtility;
using Nethermind.Evm;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// JSON converter for <see cref="Dictionary{Byte, Long}"/> that serializes opcode bytes
/// as human-readable labels (e.g., "ADD", "MUL") instead of numeric keys.
/// </summary>
public sealed class OpcodeCountsJsonConverter : JsonConverter<Dictionary<byte, long>>
{
    /// <inheritdoc />
    public override Dictionary<byte, long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object");
        }

        Dictionary<byte, long> result = [];

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name");
            }

            string propertyName = reader.GetString()!;
            byte opcodeValue = GetOpcodeByteFromName(propertyName);

            reader.Read();
            long count = reader.GetInt64();

            result[opcodeValue] = count;
        }

        throw new JsonException("Expected end of object");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Dictionary<byte, long> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach ((byte opcode, long count) in value)
        {
            string label = OpcodeLabelCache.GetLabel(opcode);
            writer.WriteNumber(label, count);
        }

        writer.WriteEndObject();
    }

    private static byte GetOpcodeByteFromName(string opcodeName)
    {
        // Handle hex format like "0xfe"
        if (opcodeName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToByte(opcodeName, 16);
        }

        // Try to parse as Instruction enum
        if (FastEnum.TryParse<Instruction>(opcodeName, ignoreCase: true, out Instruction instruction))
        {
            return (byte)instruction;
        }

        return 0;
    }
}
