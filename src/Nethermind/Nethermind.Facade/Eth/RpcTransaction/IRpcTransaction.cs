// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

public interface IRpcTransaction
{
    // TODO: Should/can we merge `JsonConverter` and `ITransactionConverter`?

    public class JsonConverter : JsonConverter<IRpcTransaction>
    {
        private readonly Type[] _transactionTypes = new Type[Transaction.MaxTxType + 1];

        public JsonConverter RegisterTransactionType(TxType type, Type @class)
        {
            _transactionTypes[(byte)type] = @class;
            return this;
        }

        public override IRpcTransaction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var jsonDocument = JsonDocument.ParseValue(ref reader);

            TxType discriminator = default;
            if (jsonDocument.RootElement.TryGetProperty("type", out JsonElement typeProperty))
            {
                discriminator = (TxType?)typeProperty.Deserialize(typeof(TxType), options) ?? default;
            }

            Type concreteTxType = _transactionTypes[(byte)discriminator];

            return (IRpcTransaction?)jsonDocument.Deserialize(concreteTxType, options);
        }

        public override void Write(Utf8JsonWriter writer, IRpcTransaction value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }

    public class TransactionConverter : ITransactionConverter<IRpcTransaction>
    {
        private readonly ITransactionConverter<IRpcTransaction>?[] _converters = new ITransactionConverter<IRpcTransaction>?[Transaction.MaxTxType + 1];

        public TransactionConverter RegisterConverter(TxType txType, ITransactionConverter<IRpcTransaction> converter)
        {
            _converters[(byte)txType] = converter;
            return this;
        }

        public IRpcTransaction FromTransaction(Transaction tx)
        {
            var converter = _converters[(byte)tx.Type] ?? throw new ArgumentException("No converter for transaction type");
            return converter.FromTransaction(tx);
        }
    }
}
