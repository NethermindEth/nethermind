// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
    public class BlockParameterConverter : JsonConverter<BlockParameter>
    {
        private NullableLongConverter _longConverter = new();

        private KeccakConverter _keccakConverter = new();

        public override void WriteJson(JsonWriter writer, BlockParameter value, JsonSerializer serializer)
        {
            if (value.Type == BlockParameterType.BlockNumber)
            {
                _longConverter.WriteJson(writer, value.BlockNumber, serializer);
                return;
            }

            if (value.Type == BlockParameterType.BlockHash)
            {
                if (value.RequireCanonical)
                {
                    writer.WriteStartObject();
                    writer.WriteProperty("requireCanonical", true);
                    writer.WriteProperty("blockHash", value.BlockHash, serializer);
                    writer.WriteEndObject();
                }
                else
                {
                    _keccakConverter.WriteJson(writer, value.BlockHash, serializer);
                }

                return;
            }

            switch (value.Type)
            {
                case BlockParameterType.Earliest:
                    writer.WriteValue("earliest");
                    break;
                case BlockParameterType.Latest:
                    writer.WriteValue("latest");
                    break;
                case BlockParameterType.Pending:
                    writer.WriteValue("pending");
                    break;
                case BlockParameterType.Finalized:
                    writer.WriteValue("finalized");
                    break;
                case BlockParameterType.Safe:
                    writer.WriteValue("safe");
                    break;
                case BlockParameterType.BlockNumber:
                    throw new InvalidOperationException("block number should be handled separately");
                case BlockParameterType.BlockHash:
                    throw new InvalidOperationException("block hash should be handled separately");
                default:
                    throw new InvalidOperationException("unknown block parameter type");
            }
        }

        private static bool IsNonStringAndLongish(object item)
        {
            return Convert.GetTypeCode(item) switch
            {
                TypeCode.Int16 => true,
                TypeCode.Int32 => true,
                TypeCode.Int64 => true,
                TypeCode.UInt16 => true,
                TypeCode.UInt64 => true,
                TypeCode.UInt32 => true,
                TypeCode.Single => true,
                TypeCode.Double => true,
                TypeCode.Decimal => true,
                TypeCode.Byte => true,
                TypeCode.SByte => true,
                _ => false
            };
        }

        public override BlockParameter ReadJson(JsonReader reader, Type objectType, BlockParameter existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.StartObject)
            {
                bool requireCanonical = false;
                Keccak blockHash = null;
                for (int i = 0; i < 2; i++)
                {
                    reader.Read();
                    if (string.Equals((string)reader.Value, "requireCanonical", StringComparison.InvariantCultureIgnoreCase))
                    {
                        requireCanonical = reader.ReadAsBoolean().Value;
                    }
                    else if (string.Equals((string)reader.Value, "blockHash", StringComparison.InvariantCultureIgnoreCase))
                    {
                        blockHash = new Keccak(reader.ReadAsString());
                    }
                }

                BlockParameter parameter = new(blockHash, requireCanonical);
                return parameter;
            }

            if (reader.Value is null)
            {
                return BlockParameter.Latest;
            }

            if (IsNonStringAndLongish(reader.Value))
            {
                return new BlockParameter((long)reader.Value);
            }

            string value = reader.Value as string;
            return GetBlockParameter(value);
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
