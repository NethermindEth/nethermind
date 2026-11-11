// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    /// <summary>Response model for <c>txpool_inspect</c>, written straight from the pool snapshot as each transaction is reached.</summary>
    [JsonConverter(typeof(TxPoolInspectionConverter))]
    public class TxPoolInspection(TxPoolInfo info, IReleaseSpec? pendingSpec)
    {
        internal TxPoolInfo Info { get; } = info;
        internal IReleaseSpec? PendingSpec { get; } = pendingSpec;
    }

    internal sealed class TxPoolInspectionConverter : JsonConverter<TxPoolInspection>
    {
        private static readonly TxPoolTxKeyConverter KeyConverter = new();

        public override TxPoolInspection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException($"{nameof(TxPoolInspectionConverter)} is serialize-only");

        public override void Write(Utf8JsonWriter writer, TxPoolInspection value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("pending"u8);
            WriteByAddress(writer, value.Info.Pending, value.PendingSpec, options);
            writer.WritePropertyName("queued"u8);
            WriteByAddress(writer, value.Info.Queued, value.PendingSpec, options);
            writer.WriteEndObject();
        }

        private static void WriteByAddress(
            Utf8JsonWriter writer,
            Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> source,
            IReleaseSpec? pendingSpec,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> byAddress in source)
            {
                writer.WritePropertyName(((Address)byAddress.Key).ToString(withZeroX: true, withEip55Checksum: true));
                writer.WriteStartObject();
                foreach (KeyValuePair<TxPoolTxKey, Transaction> kv in byAddress.Value)
                {
                    KeyConverter.WriteAsPropertyName(writer, kv.Key, options);
                    writer.WriteStringValue(GetTransactionSummary(kv.Value, pendingSpec));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        private static string GetTransactionSummary(Transaction tx, IReleaseSpec? pendingSpec)
        {
            ulong gas = pendingSpec is not null && FrameTxValidation.TryCalculateGasBudget(tx, pendingSpec, out _, out _, out ulong frameGas)
                ? frameGas
                : tx.GasLimit;
            string recipient = tx.To?.ToString(withZeroX: true, withEip55Checksum: true) ?? "contract creation";
            return $"{recipient}: {tx.Value} wei + {gas} gas × {tx.MaxFeePerGas} wei";
        }
    }
}
