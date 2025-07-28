// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin.Converters
{
    public class CapabilityConverter : JsonConverter<Capability>
    {
        public override Capability? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                ThrowJsonException();
            }

            if (TryParseCapability(ref reader, out Capability capability))
            {
                return capability;
            }

            ThrowJsonException();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, Capability value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            WriteCapability(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteCapability(Utf8JsonWriter writer, Capability capability)
        {
            writer.WriteStringValue($"{capability.ProtocolCode}/{capability.Version}");
        }

        [SkipLocalsInit]
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
            ReadOnlySpan<byte> versionSpan = valueSpan[(separatorIndex + 1)..];

            if (!Utf8Parser.TryParse(versionSpan, out int version, out _) || version < 0)
            {
                return false;
            }

            string protocolCode = System.Text.Encoding.UTF8.GetString(protocolSpan);
            capability = new Capability(protocolCode, version);
            return true;
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowJsonException() => throw new JsonException();
    }
}
