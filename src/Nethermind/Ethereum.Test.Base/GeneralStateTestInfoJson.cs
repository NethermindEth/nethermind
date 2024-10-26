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
        public string? Description { get; set; }
        public string? Url { get; set; }
        public string? Spec { get; set; }

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
            string? description = null;
            string? url = null;
            string? spec = null;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var depth = reader.CurrentDepth;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
                    {
                        break;
                    }
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("labels"u8))
                        {
                            reader.Read();
                            labels = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
                        }
                        else if (reader.ValueTextEquals("description"u8))
                        {
                            reader.Read();
                            description = JsonSerializer.Deserialize<string>(ref reader, options);
                        }
                        else if (reader.ValueTextEquals("url"u8))
                        {
                            reader.Read();
                            url = JsonSerializer.Deserialize<string>(ref reader, options);
                        }
                        else if (reader.ValueTextEquals("reference-spec"u8))
                        {
                            reader.Read();
                            spec = JsonSerializer.Deserialize<string>(ref reader, options);
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }

            return new GeneralStateTestInfoJson
            {
                Labels = labels,
                Description = description,
                Url = url,
                Spec = spec
            };
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
