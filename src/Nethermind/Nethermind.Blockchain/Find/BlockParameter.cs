// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Find
{
    using Nethermind.JsonRpc.Data;

    [JsonConverter(typeof(BlockParameterConverter))]
    public class BlockParameter : IEquatable<BlockParameter>
    {
        public static BlockParameter Earliest = new(BlockParameterType.Earliest);

        public static BlockParameter Pending = new(BlockParameterType.Pending);

        public static BlockParameter Latest = new(BlockParameterType.Latest);

        public static BlockParameter Finalized = new(BlockParameterType.Finalized);

        public static BlockParameter Safe = new(BlockParameterType.Safe);

        public BlockParameterType Type { get; }
        public long? BlockNumber { get; }

        public Hash256? BlockHash { get; }

        public bool RequireCanonical { get; }

        public BlockParameter(BlockParameterType type)
        {
            Type = type;
        }

        public BlockParameter(long number)
        {
            Type = BlockParameterType.BlockNumber;
            BlockNumber = number;
        }

        public BlockParameter(Hash256 blockHash, bool requireCanonical = false)
        {
            ArgumentNullException.ThrowIfNull(blockHash);

            Type = BlockParameterType.BlockHash;
            BlockHash = blockHash;
            RequireCanonical = requireCanonical;
        }

        public override string ToString() => $"{Type}, {BlockNumber?.ToString() ?? BlockHash?.ToString()}";

        public bool Equals(BlockParameter? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Type == other.Type && BlockNumber == other.BlockNumber && BlockHash == other.BlockHash && other.RequireCanonical == RequireCanonical;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((BlockParameter)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Type, BlockNumber, BlockHash, RequireCanonical);

        public static bool operator ==(BlockParameter? left, BlockParameter? right) => Equals(left, right);

        public static bool operator !=(BlockParameter? left, BlockParameter? right) => !Equals(left, right);
    }
}

namespace Nethermind.JsonRpc.Data
{
    using Nethermind.Blockchain.Find;
    using Nethermind.Core.Extensions;

    public class BlockParameterConverter : JsonConverter<BlockParameter>
    {
        public override bool HandleNull => true;

        public override void Write(Utf8JsonWriter writer, BlockParameter value, JsonSerializerOptions options)
        {
            if (value.Type == BlockParameterType.BlockNumber)
            {
                JsonSerializer.Serialize(writer, value.BlockNumber, options);
                return;
            }

            if (value.Type == BlockParameterType.BlockHash)
            {
                if (value.RequireCanonical)
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("requireCanonical"u8, true);
                    writer.WritePropertyName("blockHash"u8);
                    JsonSerializer.Serialize(writer, value.BlockHash, options);
                    writer.WriteEndObject();
                }
                else
                {
                    JsonSerializer.Serialize(writer, value.BlockHash, options);
                }

                return;
            }

            writer.WriteStringValue(value.Type switch
            {
                BlockParameterType.Earliest => "earliest"u8,
                BlockParameterType.Latest => "latest"u8,
                BlockParameterType.Pending => "pending"u8,
                BlockParameterType.Finalized => "finalized"u8,
                BlockParameterType.Safe => "safe"u8,
                BlockParameterType.BlockNumber => throw new InvalidOperationException("block number should be handled separately"),
                BlockParameterType.BlockHash => throw new InvalidOperationException("block hash should be handled separately"),
                _ => throw new InvalidOperationException("unknown block parameter type")
            });
        }

        [SkipLocalsInit]
        public override BlockParameter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => !reader.HasValueSequence ?
                                            reader.ValueSpan.Length <= 66 ?
                                                ReadStringFormat(reader.ValueSpan) :
                                                ReadStringComplex(ref reader, options) :
                                            ReadStringFormatValueSequence(ref reader, options),
                JsonTokenType.StartObject => ReadObjectFormat(ref reader, typeToConvert, options),
                JsonTokenType.Null => BlockParameter.Latest,
                JsonTokenType.Number when !EthereumJsonSerializer.StrictHexFormat => new BlockParameter(reader.GetInt64()),
                _ => throw new FormatException("unknown block parameter type")
            };
        }

        private BlockParameter ReadObjectFormat(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            bool requireCanonical = false;
            Hash256? blockHash = null;
            BlockParameter? blockNumberParam = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    switch (reader)
                    {
                        case var _ when reader.ValueTextEquals("requireCanonical"u8):
                            reader.Read();
                            requireCanonical = reader.GetBoolean();
                            break;
                        case var _ when reader.ValueTextEquals("blockHash"u8):
                            reader.Read();
                            blockHash = JsonSerializer.Deserialize<Hash256>(ref reader, options);
                            break;
                        case var _ when reader.ValueTextEquals("blockNumber"u8):
                            reader.Read();
                            blockNumberParam = Read(ref reader, typeToConvert, options);
                            break;
                    }
                }
            }

            return (blockHash, blockNumberParam) switch
            {
                (blockHash: not null, blockNumberParam: _) => new BlockParameter(blockHash, requireCanonical),
                (blockHash: null, blockNumberParam: not null) => blockNumberParam,
                _ => throw new FormatException("unknown block parameter type")
            };
        }

        private static BlockParameter ReadStringFormat(ReadOnlySpan<byte> span)
        {
            int length = span.Length;
            // Creates a jmp table based on length
            switch (length)
            {
                case 0:
                    // Empty string => latest
                    return BlockParameter.Latest;
                case 4:
                    if (Ascii.EqualsIgnoreCase(span, "safe"u8))
                        return BlockParameter.Safe;
                    break;
                case 6:
                    if (Ascii.EqualsIgnoreCase(span, "latest"u8))
                        return BlockParameter.Latest;
                    break;
                case 7:
                    if (Ascii.EqualsIgnoreCase(span, "pending"u8))
                        return BlockParameter.Pending;
                    break;
                case 8:
                    if (Ascii.EqualsIgnoreCase(span, "earliest"u8))
                        return BlockParameter.Earliest;
                    break;
                case 9:
                    if (Ascii.EqualsIgnoreCase(span, "finalized"u8))
                        return BlockParameter.Finalized;
                    break;
            }

            // Unknown tag or 0x quantity etc
            return ReadStringFormatOther(span);
        }

        [SkipLocalsInit]
        private static BlockParameter ReadStringFormatValueSequence(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            if (reader.ValueSequence.Length > 66)
            {
                return ReadStringComplex(ref reader, options);
            }

            Span<byte> span = stackalloc byte[66];
            int hexLength = reader.CopyString(span);
            span = span[..hexLength];

            return ReadStringFormat(span);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static BlockParameter ReadStringComplex(ref Utf8JsonReader reader, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<BlockParameter>(reader.GetString()!, options)!;

        private static BlockParameter ReadStringFormatOther(ReadOnlySpan<byte> span)
        {
            // Try hex format
            if (span.Length >= 2 && span.StartsWith("0x"u8))
            {
                span = span[2..];

                // 64 hex chars = 32 bytes = Hash256
                if (span.Length == 64)
                {
                    byte[] bytes = Bytes.FromUtf8HexString(span);
                    return new BlockParameter(new Hash256(bytes));
                }

                // Parse as block number
                long value = ParseHexNumber(span);
                return new BlockParameter(value);
            }

            // Try decimal format (if not strict)
            if (!EthereumJsonSerializer.StrictHexFormat && Utf8Parser.TryParse(span, out long decimalValue, out _))
            {
                return new BlockParameter(decimalValue);
            }

            ThrowInvalidFormatting();
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ParseHexNumber(ReadOnlySpan<byte> span)
        {
            int oddMod = span.Length % 2;
            int length = (span.Length >> 1) + oddMod;
            long value = 0;

            Span<byte> output = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
            Bytes.FromUtf8HexString(span, output[(sizeof(long) - length)..]);

            return BitConverter.IsLittleEndian switch
            {
                true => BinaryPrimitives.ReverseEndianness(value),
                _ => value
            };
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidFormatting()
            => throw new FormatException("unknown block parameter type");

        public static BlockParameter GetBlockParameter(string? value)
        {
            return value switch
            {
                null => BlockParameter.Latest,
                not null when string.IsNullOrWhiteSpace(value) => BlockParameter.Latest,
                not null when value.Equals("latest", StringComparison.OrdinalIgnoreCase) => BlockParameter.Latest,
                not null when value.Equals("earliest", StringComparison.OrdinalIgnoreCase) => BlockParameter.Earliest,
                not null when value.Equals("pending", StringComparison.OrdinalIgnoreCase) => BlockParameter.Pending,
                not null when value.Equals("finalized", StringComparison.OrdinalIgnoreCase) => BlockParameter.Finalized,
                not null when value.Equals("safe", StringComparison.OrdinalIgnoreCase) => BlockParameter.Safe,
                { Length: 66 } when value.StartsWith("0x") => new BlockParameter(new Hash256(value)),
                _ => new BlockParameter(LongConverter.FromString(value))
            };
        }
    }
}
