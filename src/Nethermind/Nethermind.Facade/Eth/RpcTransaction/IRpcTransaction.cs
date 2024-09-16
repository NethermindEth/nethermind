// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;


/// <remarks>
/// Input:
/// <para>JSON -> <see cref="RpcGenericTransaction"></see> (derived by <c>System.Text.JSON</c>)</para>
/// <para><see cref="RpcGenericTransaction"/> -> <see cref="Transaction"/> (<see cref="RpcGenericTransaction.Converter"/> with a registry of [<see cref="TxType"/> => <see cref="IToTransaction{T}"/>)</para>
/// Output:
/// <para><see cref="Transaction"/> -> <see cref="IRpcTransaction"/> (<see cref="TransactionConverter"/> with a registry of [<see cref="TxType"/> => <see cref="IFromTransaction{T}"/>)</para>
/// <para><see cref="IRpcTransaction"/> -> JSON (derived by <c>System.Text.JSON</c>.)</para>
/// </remarks>
public interface IRpcTransaction
{
    // TODO: Should/can we merge `JsonConverter` and `IFromTransaction`?
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

    public class TransactionConverter : IFromTransaction<IRpcTransaction>
    {
        private readonly IFromTransaction<IRpcTransaction>?[] _converters = new IFromTransaction<IRpcTransaction>?[Transaction.MaxTxType + 1];

        public TransactionConverter RegisterConverter(TxType txType, IFromTransaction<IRpcTransaction> converter)
        {
            _converters[(byte)txType] = converter;
            return this;
        }

        public IRpcTransaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        {
            var converter = _converters[(byte)tx.Type] ?? throw new ArgumentException("No converter for transaction type");
            return converter.FromTransaction(tx);
        }
    }
}
