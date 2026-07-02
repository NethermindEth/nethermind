// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Ethereum.Test.Base;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Evm.T8n.JsonConverters;

// required to serialize in geth t8n format
public class AccountStateJsonConverter : JsonConverter<AccountState>
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new UInt256Converter(),
            new ByteArrayConverter(),
        }
    };

    public override AccountState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => JsonSerializer.Deserialize<AccountState>(ref reader, ReadOptions);

    public override void Write(Utf8JsonWriter writer, AccountState value, JsonSerializerOptions options)
    {
        NumberConversion previousValue = ForcedNumberConversion.Value;
        try
        {
            writer.WriteStartObject();

            ForcedNumberConversion.Value = NumberConversion.Hex;
            writer.WritePropertyName("balance"u8);
            JsonSerializer.Serialize(writer, value.Balance, options);

            if (value.Nonce != UInt256.Zero)
            {
                writer.WritePropertyName("nonce"u8);
                JsonSerializer.Serialize(writer, value.Nonce, options);
            }

            if (value.Code.Length != 0)
            {
                writer.WritePropertyName("code"u8);
                JsonSerializer.Serialize(writer, value.Code, options);
            }

            ForcedNumberConversion.Value = NumberConversion.ZeroPaddedHex;
            if (value.Storage.Count > 0)
            {
                Dictionary<UInt256, UInt256> storage = value.Storage.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToUInt256());
                writer.WritePropertyName("storage"u8);
                JsonSerializer.Serialize(writer, storage, options);
            }

            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }
    }
}
