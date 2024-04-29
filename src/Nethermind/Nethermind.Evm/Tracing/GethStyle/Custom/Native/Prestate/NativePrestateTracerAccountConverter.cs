// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

public class NativePrestateTracerAccountConverter : JsonConverter<NativePrestateTracerAccount>
{
    public override void Write(Utf8JsonWriter writer, NativePrestateTracerAccount value, JsonSerializerOptions options)
    {
        NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
        try
        {
            writer.WriteStartObject();

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
            writer.WritePropertyName("balance"u8);
            JsonSerializer.Serialize(writer, value.Balance, options);

            if (value.IsPrestate)
            {
                ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Decimal;
            }
            else
            {
                ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
            }

            if (value.Nonce is not null)
            {
                writer.WritePropertyName("nonce"u8);
                JsonSerializer.Serialize(writer, value.Nonce, options);
            }

            if (value.Code is not null)
            {
                writer.WritePropertyName("code"u8);
                JsonSerializer.Serialize(writer, value.Code, options);
            }

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.ZeroPaddedHex;
            if (value.Storage is not null)
            {
                writer.WritePropertyName("storage"u8);
                JsonSerializer.Serialize(writer, value.Storage, options);
            }

            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.ForcedConversion.Value = previousValue;
        }
    }

    public override NativePrestateTracerAccount? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
