// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public sealed class StorageValueConverter : JsonConverter<StorageValue>
    {
        [SkipLocalsInit]
        public override StorageValue Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            ReadOnlySpan<byte> hex = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            if (hex.StartsWith("0x"u8))
            {
                hex = hex[2..];
            }

            if (hex.Length > 64)
            {
                throw new JsonException();
            }

            return StorageValue.FromHexString(hex);
        }

        public override void Write(
            Utf8JsonWriter writer,
            StorageValue value,
            JsonSerializerOptions options)
        {
            ByteArrayConverter.Convert(writer, value.Bytes, skipLeadingZeros: false);
        }
    }
}
