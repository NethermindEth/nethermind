// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

using System.Text.Json;
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

        public Keccak? BlockHash { get; }

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

        public BlockParameter(Keccak blockHash, bool requireCanonical = false)
        {
            Type = BlockParameterType.BlockHash;
            BlockHash = blockHash;
            RequireCanonical = requireCanonical;
        }

        public override string ToString() => $"{Type}, {BlockNumber?.ToString() ?? BlockHash?.ToString()}";

        public bool Equals(BlockParameter? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Type == other.Type && BlockNumber == other.BlockNumber && BlockHash == other.BlockHash && other.RequireCanonical == RequireCanonical;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((BlockParameter)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Type, BlockNumber, BlockHash, RequireCanonical);
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

            switch (value.Type)
            {
                case BlockParameterType.Earliest:
                    writer.WriteStringValue("earliest"u8);
                    break;
                case BlockParameterType.Latest:
                    writer.WriteStringValue("latest"u8);
                    break;
                case BlockParameterType.Pending:
                    writer.WriteStringValue("pending"u8);
                    break;
                case BlockParameterType.Finalized:
                    writer.WriteStringValue("finalized"u8);
                    break;
                case BlockParameterType.Safe:
                    writer.WriteStringValue("safe"u8);
                    break;
                case BlockParameterType.BlockNumber:
                    throw new InvalidOperationException("block number should be handled separately");
                case BlockParameterType.BlockHash:
                    throw new InvalidOperationException("block hash should be handled separately");
                default:
                    throw new InvalidOperationException("unknown block parameter type");
            }
        }

        [SkipLocalsInit]
        public override BlockParameter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            JsonTokenType tokenType = reader.TokenType;
            if (tokenType == JsonTokenType.String && (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > 66)
            {
                return JsonSerializer.Deserialize<BlockParameter>(reader.GetString(), options);
            }
            if (tokenType == JsonTokenType.StartObject)
            {
                bool requireCanonical = false;
                Keccak blockHash = null;
                for (int i = 0; i < 2; i++)
                {
                    reader.Read();
                    if (reader.ValueTextEquals("requireCanonical"u8))
                    {
                        reader.Read();
                        requireCanonical = reader.GetBoolean();
                    }
                    else if (reader.ValueTextEquals("blockHash"u8))
                    {
                        blockHash = JsonSerializer.Deserialize<Keccak>(ref reader, options);
                    }
                }

                BlockParameter parameter = new(blockHash, requireCanonical);

                if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
                {
                    ThrowInvalidFormatting();
                }

                return parameter;
            }

            if (tokenType == JsonTokenType.Null)
            {
                return BlockParameter.Latest;
            }
            if (tokenType == JsonTokenType.Number)
            {
                return new BlockParameter(reader.GetInt64());
            }

            if (tokenType != JsonTokenType.String)
            {
                ThrowInvalidFormatting();
            }

            if (reader.ValueTextEquals(ReadOnlySpan<byte>.Empty) || reader.ValueTextEquals("latest"u8))
            {
                return BlockParameter.Latest;
            }
            else if (reader.ValueTextEquals("earliest"u8))
            {
                return BlockParameter.Earliest;
            }
            else if (reader.ValueTextEquals("pending"u8))
            {
                return BlockParameter.Pending;
            }
            else if (reader.ValueTextEquals("finalized"u8))
            {
                return BlockParameter.Finalized;
            }
            else if (reader.ValueTextEquals("safe"u8))
            {
                return BlockParameter.Safe;
            }

            Span<byte> span = stackalloc byte[66];
            int hexLength = reader.CopyString(span);
            span = span[..hexLength];

            long value = 0;
            if (span.Length >= 2 && span.StartsWith("0x"u8))
            {
                span = span[2..];
                if (span.Length == 64)
                {
                    byte[] bytes = Bytes.FromUtf8HexString(span);
                    return new BlockParameter(new Keccak(bytes));
                }

                int oddMod = span.Length % 2;
                int length = (span.Length >> 1) + oddMod;

                Span<byte> output = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));

                Bytes.FromUtf8HexString(span, output[(sizeof(long) - length)..]);

                if (BitConverter.IsLittleEndian)
                {
                    value = BinaryPrimitives.ReverseEndianness(value);
                }

                return new BlockParameter(value);
            }

            if (Utf8Parser.TryParse(span, out value, out _))
            {
                return new BlockParameter(value);
            }

            // Lower case the span string
            for (int i = 0; i < span.Length; i++)
            {
                int ch = span[i];
                if (ch >= 'A' && ch <= 'Z')
                {
                    span[i] = (byte)(ch + 'a' - 'A');
                }
            }

            if (span.SequenceEqual("latest"u8))
            {
                return BlockParameter.Latest;
            }
            else if (span.SequenceEqual("earliest"u8))
            {
                return BlockParameter.Earliest;
            }
            else if (span.SequenceEqual("pending"u8))
            {
                return BlockParameter.Pending;
            }
            else if (span.SequenceEqual("finalized"u8))
            {
                return BlockParameter.Finalized;
            }
            else if (span.SequenceEqual("safe"u8))
            {
                return BlockParameter.Safe;
            }

            ThrowInvalidFormatting();
            return null;
        }

        private static void ThrowInvalidFormatting()
        {
            throw new InvalidOperationException("unknown block parameter type");
        }

        public static BlockParameter GetBlockParameter(string? value)
        {
            switch (value)
            {
                case null:
                case { } empty when string.IsNullOrWhiteSpace(empty):
                case { } latest when latest.Equals("latest", StringComparison.InvariantCultureIgnoreCase):
                    return BlockParameter.Latest;
                case { } earliest when earliest.Equals("earliest", StringComparison.InvariantCultureIgnoreCase):
                    return BlockParameter.Earliest;
                case { } pending when pending.Equals("pending", StringComparison.InvariantCultureIgnoreCase):
                    return BlockParameter.Pending;
                case { } finalized when finalized.Equals("finalized", StringComparison.InvariantCultureIgnoreCase):
                    return BlockParameter.Finalized;
                case { } safe when safe.Equals("safe", StringComparison.InvariantCultureIgnoreCase):
                    return BlockParameter.Safe;
                case { Length: 66 } hash when hash.StartsWith("0x"):
                    return new BlockParameter(new Keccak(hash));
                default:
                    return new BlockParameter(LongConverter.FromString(value));
            }
        }
    }
}
