using Ethereum.Test.Base;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Evm.T8NTool;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

public class AccountStateJsonConverter : JsonConverter<AccountState>
{
    private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();
    private readonly UInt256Converter _uInt256Converter = new();
    private readonly Hash256Converter _hash256Converter = new();
    public override AccountState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return _ethereumJsonSerializer.Deserialize<AccountState>(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, AccountState accountState, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (accountState.Code != null)
        {
            writer.WritePropertyName("code"u8);
            ByteArrayConverter.Convert(writer, accountState.Code);
        }

        if (!accountState.Balance.IsZero)
        {
            writer.WritePropertyName("balance"u8);
            _uInt256Converter.Write(writer, accountState.Balance, options);
        }

        if (!accountState.Nonce.IsZero)
        {
            writer.WritePropertyName("nonce"u8);
            _uInt256Converter.Write(writer, accountState.Nonce, options);
        }

        if (!accountState.Storage.IsNullOrEmpty())
        {
            writer.WritePropertyName("storage"u8);
            var storage = accountState.Storage.ToDictionary(pair => Utils.ConvertToHash256(pair.Key), pair => Utils.ConvertToHash256(pair.Value));
            writer.WriteStartObject();

            foreach (var kvp in storage)
            {
                writer.WritePropertyName(kvp.Key.ToString());
                _hash256Converter.Write(writer, kvp.Value, options);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
