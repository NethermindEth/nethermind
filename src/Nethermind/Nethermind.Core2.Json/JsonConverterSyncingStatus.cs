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
using Nethermind.Core2.Api;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterSyncingStatus : JsonConverter<SyncingStatus>
    {
        private static JsonEncodedText _currentSlotName;
        private static JsonEncodedText _highestSlotName;
        private static JsonEncodedText _startingSlotName;

        public override SyncingStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            EnsureNames(options);
            // Standard deserializer can write values directly into object, although a setter would still create and then pass in 
            var properties = new object[3];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals(_currentSlotName.EncodedUtf8Bytes))
                    {
                        reader.Read();
                        properties[0] = JsonSerializer.Deserialize<Slot>(ref reader, options);
                    }
                    else if (reader.ValueTextEquals(_highestSlotName.EncodedUtf8Bytes))
                    {
                        reader.Read();
                        properties[1] = JsonSerializer.Deserialize<Slot>(ref reader, options);
                    }
                    else if (reader.ValueTextEquals(_startingSlotName.EncodedUtf8Bytes))
                    {
                        reader.Read();
                        properties[2] = JsonSerializer.Deserialize<Slot>(ref reader, options);
                    }
                }
            }

            return new SyncingStatus((Slot) properties[2], (Slot) properties[0], (Slot) properties[1]);
        }

        public override void Write(Utf8JsonWriter writer, SyncingStatus value, JsonSerializerOptions options)
        {
            EnsureNames(options);
            // Default is alphabetical order
            writer.WriteStartObject();
            writer.WriteNumber(_currentSlotName, value.CurrentSlot);
            writer.WriteNumber(_highestSlotName, value.HighestSlot);
            writer.WriteNumber(_startingSlotName, value.StartingSlot);
            writer.WriteEndObject();
        }

        private void EnsureNames(JsonSerializerOptions options)
        {
            if (_currentSlotName.Equals(default))
            {
                _currentSlotName =
                    JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(nameof(SyncingStatus.Zero.CurrentSlot)),
                        options.Encoder);
                _highestSlotName =
                    JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(nameof(SyncingStatus.Zero.HighestSlot)),
                        options.Encoder);
                _startingSlotName =
                    JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(nameof(SyncingStatus.Zero.StartingSlot)),
                        options.Encoder);
            }
        }
    }
}