// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Buffers.Text;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class BooleanConverter : JsonConverter<bool>
    {
        public override bool Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.False)
            {
                return false;
            }
            else if (reader.TokenType == JsonTokenType.True)
            {
                return true;
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                if (!reader.HasValueSequence)
                {
                    if (Utf8Parser.TryParse(reader.ValueSpan, out bool value, out _))
                    {
                        return value;
                    }
                }
                else
                {
                    if (Utf8Parser.TryParse(reader.ValueSequence.ToArray(), out bool value, out _))
                    {
                        return value;
                    }
                }
            }

            throw new InvalidOperationException();
        }

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            bool value,
            JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }
}
