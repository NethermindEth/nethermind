//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityAccountStateChangeConverter : JsonConverter<ParityAccountStateChange>
    {
        private ByteArrayConverter _bytesConverter = new();
        private NullableUInt256Converter _intConverter = new();
        private Bytes32Converter _32BytesConverter = new();

        private void WriteChange(JsonWriter writer, ParityStateChange<byte[]> change, JsonSerializer serializer)
        {
            if (change == null)
            {
                writer.WriteValue("=");
            }
            else
            {
                if (change.Before == null)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("+");
                    _bytesConverter.WriteJson(writer, change.After, serializer);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("*");
                    writer.WriteStartObject();
                    writer.WritePropertyName("from");
                    _bytesConverter.WriteJson(writer, change.Before, serializer);
                    writer.WritePropertyName("to");
                    _bytesConverter.WriteJson(writer, change.After, serializer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
        }

        private void WriteChange(JsonWriter writer, ParityStateChange<UInt256?> change, JsonSerializer serializer)
        {
            if (change == null)
            {
                writer.WriteValue("=");
            }
            else
            {
                if (change.Before == null)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("+");
                    _intConverter.WriteJson(writer, change.After, serializer);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("*");
                    writer.WriteStartObject();
                    writer.WritePropertyName("from");
                    _intConverter.WriteJson(writer, change.Before, serializer);
                    writer.WritePropertyName("to");
                    _intConverter.WriteJson(writer, change.After, serializer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
        }

        private void WriteStorageChange(JsonWriter writer, ParityStateChange<byte[]> change, bool isNew, JsonSerializer serializer)
        {
            if (change == null)
            {
                writer.WriteValue("=");
            }
            else
            {
                if (isNew)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("+");
                    _32BytesConverter.WriteJson(writer, change.After, serializer);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("*");
                    writer.WriteStartObject();
                    writer.WritePropertyName("from");
                    _32BytesConverter.WriteJson(writer, change.Before, serializer);
                    writer.WritePropertyName("to");
                    _32BytesConverter.WriteJson(writer, change.After, serializer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
        }

        public override void WriteJson(JsonWriter writer, ParityAccountStateChange value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("balance");
            if (value.Balance == null)
            {
                writer.WriteValue("=");
            }
            else
            {
                WriteChange(writer, value.Balance, serializer);
            }

            writer.WritePropertyName("code");
            if (value.Code == null)
            {
                writer.WriteValue("=");
            }
            else
            {
                WriteChange(writer, value.Code, serializer);
            }

            writer.WritePropertyName("nonce");
            if (value.Nonce == null)
            {
                writer.WriteValue("=");
            }
            else
            {
                WriteChange(writer, value.Nonce, serializer);
            }

            writer.WritePropertyName("storage");

            writer.WriteStartObject();
            if (value.Storage != null)
            {
                foreach (KeyValuePair<UInt256, ParityStateChange<byte[]>> pair in value.Storage.OrderBy(s => s.Key))
                {
                    string trimmedKey = pair.Key.ToString("x64");
                    trimmedKey = trimmedKey.Substring(trimmedKey.Length - 64, 64);
                    
                    writer.WritePropertyName(string.Concat("0x", trimmedKey));
                    WriteStorageChange(writer, pair.Value, value.Balance?.Before == null && value.Balance?.After != null, serializer);
                }
            }

            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        public override ParityAccountStateChange ReadJson(JsonReader reader, Type objectType, ParityAccountStateChange existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
