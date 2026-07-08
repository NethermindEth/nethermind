// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Polymorphic converter rooted at <see cref="LegacyTransactionForRpc"/>, the base of every
/// user-signable transaction type. RPC methods taking a transaction as input can declare the
/// parameter as <see cref="LegacyTransactionForRpc"/> to reject output-only types (e.g. Optimism
/// deposit transactions) at deserialization while keeping the polymorphic dispatch of
/// <see cref="TransactionForRpc"/>.
/// </summary>
public sealed class LegacyTransactionJsonConverter : JsonConverter<LegacyTransactionForRpc>
{
    /// <remarks>
    /// Registered on assembly load: the converter must already be present in the serializer options
    /// when the first request binds a <see cref="LegacyTransactionForRpc"/>-declared parameter, in
    /// any host (client, plugins, tests).
    /// </remarks>
    [ModuleInitializer]
    internal static void Register() => EthereumJsonSerializer.AddConverter(new LegacyTransactionJsonConverter());

    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _withoutSelfCache = new();

    /// <summary>
    /// Returns a copy of <paramref name="options"/> without this converter, letting the polymorphic
    /// dispatch deserialize the concrete <see cref="LegacyTransactionForRpc"/> type plainly instead
    /// of recursing back into this converter.
    /// </summary>
    internal static JsonSerializerOptions WithoutSelf(JsonSerializerOptions options)
    {
        foreach (JsonConverter converter in options.Converters)
        {
            if (converter is LegacyTransactionJsonConverter)
            {
                return _withoutSelfCache.GetValue(options, static o =>
                {
                    JsonSerializerOptions copy = new(o);
                    for (int i = copy.Converters.Count - 1; i >= 0; i--)
                    {
                        if (copy.Converters[i] is LegacyTransactionJsonConverter)
                        {
                            copy.Converters.RemoveAt(i);
                        }
                    }
                    return copy;
                });
            }
        }

        return options;
    }

    public override LegacyTransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        TransactionForRpc? tx = JsonSerializer.Deserialize<TransactionForRpc>(ref reader, options);
        return tx switch
        {
            null => null,
            LegacyTransactionForRpc legacy => legacy,
            _ => throw new JsonException($"transaction type {tx.Type} is not supported as an input")
        };
    }

    public override void Write(Utf8JsonWriter writer, LegacyTransactionForRpc value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), WithoutSelf(options));
}
