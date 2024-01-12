// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ethereum.Test.Base
{
    [JsonConverter(typeof(GeneralStateTestInfoConverter))]
    public class GeneralStateTestInfoJson
    {
        public Dictionary<string, string>? Labels { get; set; }
    }

    public class GeneralStateTestInfoConverter : JsonConverter<GeneralStateTestInfoJson>
    {
        public override GeneralStateTestInfoJson? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            Dictionary<string, string>? labels = null;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var depth = reader.CurrentDepth;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
                    {
                        break;
                    }
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("labels"u8))
                    {
                        reader.Read();
                        labels = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }

            return new GeneralStateTestInfoJson { Labels = labels };
        }

        public override void Write(
            Utf8JsonWriter writer,
            GeneralStateTestInfoJson info,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, info, options);
        }
    }
}
