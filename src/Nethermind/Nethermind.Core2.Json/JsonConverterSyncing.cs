//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core2.Api;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterSyncing : JsonConverter<Syncing>
    {
        private static JsonEncodedText[]? _propertyNames;

        public override Syncing Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            EnsureNames(options);
            var properties = new object?[2];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals(_propertyNames![0].EncodedUtf8Bytes))
                    {
                        reader.Read();
                        properties[0] = reader.GetBoolean();
                    }
                    else if (reader.ValueTextEquals(_propertyNames[1].EncodedUtf8Bytes))
                    {
                        reader.Read();
                        properties[1] = JsonSerializer.Deserialize<SyncingStatus?>(ref reader, options);
                    }
                }
            }

            return new Syncing((bool)properties[0]!, properties[1] as SyncingStatus);
        }

        public override void Write(Utf8JsonWriter writer, Syncing value, JsonSerializerOptions options)
        {
            EnsureNames(options);
            writer.WriteStartObject();
            writer.WriteBoolean(_propertyNames![0], value.IsSyncing);
            writer.WritePropertyName(_propertyNames[1]);
            JsonSerializer.Serialize(writer, value.SyncStatus, options);
            writer.WriteEndObject();
        }
        
        private void EnsureNames(JsonSerializerOptions options)
        {
            if (_propertyNames is null)
            {
                JsonEncodedText[] propertyNames = new JsonEncodedText[2];
                Syncing names;
                propertyNames[0] =
                    JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(nameof(names.IsSyncing)),
                        options.Encoder);
                propertyNames[1] =
                    JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(nameof(names.SyncStatus)),
                        options.Encoder);
                Interlocked.CompareExchange(ref _propertyNames, propertyNames, null);
            }
        }
        
    }
}