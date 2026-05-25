// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.Serialization.Json;

public static class ForcedNumberConversion
{
    [ThreadStatic]
    private static NumberConversion _threadCache;

    public static NumberConversion Value
    {
        get => _threadCache;
        set => _threadCache = value;
    }

    public static void WriteRawLong(Utf8JsonWriter writer, ReadOnlySpan<byte> name, long value)
    {
        writer.WritePropertyName(name);

        NumberConversion previous = Value;
        Value = NumberConversion.Raw;
        try
        {
            JsonSerializer.Serialize(writer, value, EthereumJsonSerializer.JsonOptions);
        }
        finally
        {
            Value = previous;
        }
    }
}
