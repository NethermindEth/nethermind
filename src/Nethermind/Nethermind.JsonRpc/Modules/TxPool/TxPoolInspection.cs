// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Serialization.Json;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    /// <summary>Response model for <c>txpool_inspect</c>, written straight from the pool snapshot as each transaction is reached.</summary>
    [JsonConverter(typeof(TxPoolInspectionConverter))]
    public class TxPoolInspection(TxPoolInfo info)
    {
        internal TxPoolInfo Info { get; } = info;
    }

    internal sealed class TxPoolInspectionConverter : JsonConverter<TxPoolInspection>
    {
        private static readonly AddressAsKeyConverter AddressConverter = new();
        private static readonly TxPoolTxKeyConverter KeyConverter = new();

        public override TxPoolInspection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException($"{nameof(TxPoolInspectionConverter)} is serialize-only");

        public override void Write(Utf8JsonWriter writer, TxPoolInspection value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("pending"u8);
            WriteByAddress(writer, value.Info.Pending, options);
            writer.WritePropertyName("queued"u8);
            WriteByAddress(writer, value.Info.Queued, options);
            writer.WriteEndObject();
        }

        private static void WriteByAddress(
            Utf8JsonWriter writer,
            Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> source,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> byAddress in source)
            {
                AddressConverter.WriteAsPropertyName(writer, byAddress.Key, options);
                writer.WriteStartObject();
                foreach (KeyValuePair<TxPoolTxKey, Transaction> kv in byAddress.Value)
                {
                    KeyConverter.WriteAsPropertyName(writer, kv.Key, options);
                    writer.WriteStringValue(GetTransactionSummary(kv.Value));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        private static string GetTransactionSummary(Transaction tx)
            => $"{tx.SenderAddress}: {tx.Value} wei + {tx.GasLimit} × {tx.GasPrice} gas";
    }
}
