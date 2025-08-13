// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Network.Contract.P2P;

namespace Nethermind.Stats.Model
{
    public class CapabilityConverter : JsonConverter<Capability>
    {
        private const int MaxIntegerDigits = 10; // int.MaxValue has 10 digits
        private const byte SeparatorByte = (byte)'/';
        private const int StackAllocThreshold = 256;

        public override Capability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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
            int protocolByteCount = Encoding.UTF8.GetByteCount(capability.ProtocolCode);
            int totalLength = protocolByteCount + 1 + MaxIntegerDigits;

            if (totalLength <= StackAllocThreshold)
            {
                Span<byte> buffer = stackalloc byte[totalLength];
                WriteToBuffer(writer, capability, buffer, protocolByteCount);
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(totalLength);
                try
                {
                    WriteToBuffer(writer, capability, rented.AsSpan(), protocolByteCount);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteToBuffer(Utf8JsonWriter writer, Capability capability, Span<byte> buffer, int protocolByteCount)
        {
            Encoding.UTF8.GetBytes(capability.ProtocolCode, buffer);
            buffer[protocolByteCount] = SeparatorByte;

            if (Utf8Formatter.TryFormat(capability.Version, buffer[(protocolByteCount + 1)..], out int versionBytes))
            {
                writer.WriteStringValue(buffer[..(protocolByteCount + 1 + versionBytes)]);
            }
            else
            {
                ThrowJsonException();
            }
        }

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

            int separatorIndex = valueSpan.IndexOf(SeparatorByte);
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

            string protocolCode = GetProtocolCode(protocolSpan);
            capability = new Capability(protocolCode, version);
            return true;
        }

        private static string GetProtocolCode(ReadOnlySpan<byte> protocolSpan)
        {
            if (protocolSpan.SequenceEqual("eth"u8)) return Protocol.Eth;
            if (protocolSpan.SequenceEqual("snap"u8)) return Protocol.Snap;
            if (protocolSpan.SequenceEqual("p2p"u8)) return Protocol.P2P;
            if (protocolSpan.SequenceEqual("nodedata"u8)) return Protocol.NodeData;
            if (protocolSpan.SequenceEqual("shh"u8)) return Protocol.Shh;
            if (protocolSpan.SequenceEqual("bzz"u8)) return Protocol.Bzz;
            if (protocolSpan.SequenceEqual("par"u8)) return Protocol.Par;
            if (protocolSpan.SequenceEqual("ndm"u8)) return Protocol.Ndm;
            if (protocolSpan.SequenceEqual("aa"u8)) return Protocol.AA;
            
            // Fallback for unknown protocols
            return Encoding.UTF8.GetString(protocolSpan);
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowJsonException() => throw new JsonException();
    }
}