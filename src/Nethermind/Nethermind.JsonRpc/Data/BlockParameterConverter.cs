/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core.Json;
using Newtonsoft.Json;
using NLog.StructuredLogging.Json.Helpers;

namespace Nethermind.JsonRpc.Data
{
    public class BlockParameterConverter : JsonConverter<BlockParameter>
    {
        private NullableLongConverter _longConverter = new NullableLongConverter();
        
        public override void WriteJson(JsonWriter writer, BlockParameter value, JsonSerializer serializer)
        {
            if (value.Type == BlockParameterType.BlockNumber)
            {
                _longConverter.WriteJson(writer, value.BlockNumber, serializer);
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
                case BlockParameterType.BlockNumber:
                    throw new InvalidOperationException("block number should be handled separately");
                default:
                    throw new InvalidOperationException("unknown block parameter type");
            }
        }

        public override BlockParameter ReadJson(JsonReader reader, Type objectType, BlockParameter existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.Value == null)
            {
                return BlockParameter.Latest;
            }
            
            if (reader.IsNonStringValueType())
            {
                return new BlockParameter((long)reader.Value);
            }
                
            string value = reader.Value as string;
            return value switch
            {
                "" => BlockParameter.Latest,
                "latest" => BlockParameter.Latest,
                "earliest" => BlockParameter.Earliest,
                "pending" => BlockParameter.Pending,
                _ => new BlockParameter(LongConverter.FromString(value))
            };
        }
    }
}