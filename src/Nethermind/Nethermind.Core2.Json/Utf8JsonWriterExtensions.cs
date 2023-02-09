// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.Core2.Json
{
    public static class Utf8JsonWriterExtensions
    {
        public static void WritePrefixedHexStringValue(this Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
        {
            // TODO: should add faster version that writes directly to Writer (zero allocation).
            string hex = bytes.ToHexString(withZeroX: true);
            writer.WriteStringValue(hex);
        }
    }
}
