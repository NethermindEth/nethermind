// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class LongRawJsonConverter : JsonConverter<long>
    {
        public override long Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            long value,
            JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
