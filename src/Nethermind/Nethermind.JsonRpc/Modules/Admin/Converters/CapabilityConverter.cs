// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin.Converters
{
    public class CapabilityConverter : JsonConverter<IReadOnlyList<Capability>>
    {
        public override bool HandleNull => false;

        [SkipLocalsInit]
        public override IReadOnlyList<Capability>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                ThrowInvalidToken("Expected StartArray token");
            }

            var capabilities = new List<Capability>();
            var depth = reader.CurrentDepth;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    if (TryParseCapability(ref reader, out Capability capability))
                    {
                        capabilities.Add(capability);
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return capabilities;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<Capability> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();

            for (int i = 0; i < value.Count; i++)
            {
                WriteCapabilityValue(writer, value[i]);
            }

            writer.WriteEndArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteCapabilityValue(Utf8JsonWriter writer, Capability capability)
        {
            writer.WriteStringValue($"{capability.ProtocolCode}/{capability.Version}");
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseCapability(ref Utf8JsonReader reader, out Capability capability)
        {
            capability = default;

            ReadOnlySpan<byte> valueSpan = reader.HasValueSequence ?
                reader.ValueSequence.ToArray() :
                reader.ValueSpan;

            if (valueSpan.Length == 0)
            {
                return false;
            }

            int separatorIndex = valueSpan.IndexOf((byte)'/');
            if (separatorIndex <= 0 || separatorIndex >= valueSpan.Length - 1)
            {
                return false;
            }

            ReadOnlySpan<byte> protocolSpan = valueSpan[..separatorIndex];
            string protocolCode = System.Text.Encoding.UTF8.GetString(protocolSpan);

            ReadOnlySpan<byte> versionSpan = valueSpan[(separatorIndex + 1)..];
            if (!Utf8Parser.TryParse(versionSpan, out int version, out _))
            {
                return false;
            }

            capability = new Capability(protocolCode, version);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidToken(string message)
        {
            throw new JsonException(message);
        }
    }
}
