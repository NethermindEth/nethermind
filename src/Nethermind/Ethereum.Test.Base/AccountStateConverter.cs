using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Ethereum.Test.Base;

using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

public class AccountStateConverter : JsonConverter<AccountState>
{
    private EthereumJsonSerializer _ethereumJsonSerializer = new();

    public override AccountState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return _ethereumJsonSerializer.Deserialize<AccountState>(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, AccountState value, JsonSerializerOptions options)
    {
        NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
        try
        {
            writer.WriteStartObject();

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
            if (value.Balance != UInt256.Zero)
            {
                writer.WritePropertyName("balance"u8);
                JsonSerializer.Serialize(writer, value.Balance, options);
            }

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
            if (value.Nonce != UInt256.Zero)
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
            if (value.Storage?.Count > 0)
            {
                Dictionary<UInt256, UInt256> storage =
                    value.Storage.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToUInt256());
                writer.WritePropertyName("storage"u8);
                JsonSerializer.Serialize(writer, storage, options);
            }

            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.ForcedConversion.Value = previousValue;
        }
    }
}
