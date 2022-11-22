// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Core2.Json
{
    public static class Utf8JsonReaderExtensions
    {
        public static byte[] GetBytesFromPrefixedHex(this Utf8JsonReader reader)
        {
            // TODO: Rather than get the string first, convert directly from reader to bytes (minimal allocation)
            string hex = reader.GetString();
            byte[] bytes = Bytes.FromHexString(hex);
            return bytes;
        }
    }
}
