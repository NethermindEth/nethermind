// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>Writes RPC transactions through the generated writers of the converter that writes them.</summary>
internal static class TransactionForRpcWriterExtensions
{
    /// <summary>Writes <paramref name="value"/> as its runtime type, through its generated writer in <paramref name="writers"/> when it has one.</summary>
    /// <param name="writers">The calling converter's own writers, so their state lives as long as the options it serves.</param>
    public static void WriteAsRuntimeType(this Utf8JsonWriter writer, TransactionForRpc value, GeneratedJsonDispatch writers, JsonSerializerOptions options)
    {
        Type type = value.GetType();
        if (writers.TryGetWriter(type, out IGeneratedJsonWriter? generated))
        {
            generated.WriteValue(writer, value, options);
        }
        else
        {
            TypeInfoJsonSerializer.Serialize(writer, value, type, options);
        }
    }
}
