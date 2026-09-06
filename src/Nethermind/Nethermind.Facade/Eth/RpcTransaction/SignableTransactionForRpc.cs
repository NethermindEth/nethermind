// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Abstract root of the user-signable transaction types (legacy, access list, EIP-1559, blob,
/// set-code). Declaring an RPC input parameter as this type restricts it, via the type system, to
/// transactions a user can submit: output-only types such as Optimism deposits derive from
/// <see cref="TransactionForRpc"/> directly and are rejected during deserialization.
/// </summary>
/// <remarks>
/// The root is abstract on purpose. Because it is never a concrete leaf, the polymorphic converter
/// never has to deserialize its own type and needs no self-exclusion — unlike a converter rooted at
/// a concrete type. The converter is attached via <see cref="JsonConverterAttribute"/> rather than
/// registered globally, so it applies only where a parameter is declared as this type.
/// </remarks>
[JsonConverter(typeof(SignableTransactionJsonConverter))]
public abstract class SignableTransactionForRpc : TransactionForRpc
{
    protected SignableTransactionForRpc() { }

    protected SignableTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData) { }

    internal sealed class SignableTransactionJsonConverter : JsonConverter<SignableTransactionForRpc>
    {
        public override SignableTransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            // The base converter matches only TransactionForRpc, so its concrete-type deserialization never re-enters this one.
            JsonSerializer.Deserialize<TransactionForRpc>(ref reader, options) switch
            {
                null => null,
                SignableTransactionForRpc signable => signable,
                { Type: var type } => throw new JsonException($"transaction type {type} is not supported as an input")
            };

        public override void Write(Utf8JsonWriter writer, SignableTransactionForRpc value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
