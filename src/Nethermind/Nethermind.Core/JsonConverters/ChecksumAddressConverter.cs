// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.Json;
using Nethermind.Core;

namespace Nethermind.Serialization.Json;

public class ChecksumAddressConverter : AddressConverter
{
    public override void Write(
        Utf8JsonWriter writer,
        Address address,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(address.ToString(true, true));
    }

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer,
        Address value,
        JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString(true, true));
    }
}
