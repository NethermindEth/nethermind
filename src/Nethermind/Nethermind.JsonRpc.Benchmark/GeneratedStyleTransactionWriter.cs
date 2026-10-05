// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Hand-written stand-in for what a Nethermind source generator would emit for <see cref="EIP1559TransactionForRpc"/>:
/// properties in metadata order, each written through the converter the options resolve for its type.
/// </summary>
internal sealed class GeneratedStyleTransactionWriter : JsonConverter<EIP1559TransactionForRpc>
{
    private static readonly JsonEncodedText TypeName = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText MaxPriorityFeePerGasName = JsonEncodedText.Encode("maxPriorityFeePerGas");
    private static readonly JsonEncodedText MaxFeePerGasName = JsonEncodedText.Encode("maxFeePerGas");
    private static readonly JsonEncodedText AccessListName = JsonEncodedText.Encode("accessList");
    private static readonly JsonEncodedText YParityName = JsonEncodedText.Encode("yParity");
    private static readonly JsonEncodedText ChainIdName = JsonEncodedText.Encode("chainId");
    private static readonly JsonEncodedText NonceName = JsonEncodedText.Encode("nonce");
    private static readonly JsonEncodedText ToName = JsonEncodedText.Encode("to");
    private static readonly JsonEncodedText FromName = JsonEncodedText.Encode("from");
    private static readonly JsonEncodedText ValueName = JsonEncodedText.Encode("value");
    private static readonly JsonEncodedText InputName = JsonEncodedText.Encode("input");
    private static readonly JsonEncodedText GasPriceName = JsonEncodedText.Encode("gasPrice");
    private static readonly JsonEncodedText VName = JsonEncodedText.Encode("v");
    private static readonly JsonEncodedText RName = JsonEncodedText.Encode("r");
    private static readonly JsonEncodedText SName = JsonEncodedText.Encode("s");
    private static readonly JsonEncodedText HashName = JsonEncodedText.Encode("hash");
    private static readonly JsonEncodedText TransactionIndexName = JsonEncodedText.Encode("transactionIndex");
    private static readonly JsonEncodedText BlockHashName = JsonEncodedText.Encode("blockHash");
    private static readonly JsonEncodedText BlockNumberName = JsonEncodedText.Encode("blockNumber");
    private static readonly JsonEncodedText BlockTimestampName = JsonEncodedText.Encode("blockTimestamp");
    private static readonly JsonEncodedText GasName = JsonEncodedText.Encode("gas");

    // Property-level [JsonConverter] attributes name these types; the generator would emit them as statics.
    private static readonly StrictHexByteArrayConverter StrictHex = new();
    private static readonly NullableUInt256Converter SignatureWord = new();

    private Converters? _converters;

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(EIP1559TransactionForRpc);

    public override EIP1559TransactionForRpc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, EIP1559TransactionForRpc value, JsonSerializerOptions options)
    {
        Converters c = GetConverters(options);

        writer.WriteStartObject();
        writer.WritePropertyName(TypeName);
        c.TxType.Write(writer, value.Type, options);
        writer.WritePropertyName(MaxPriorityFeePerGasName);
        c.UInt256.Write(writer, value.MaxPriorityFeePerGas, options);
        writer.WritePropertyName(MaxFeePerGasName);
        c.UInt256.Write(writer, value.MaxFeePerGas, options);
        writer.WritePropertyName(AccessListName);
        WriteReference(writer, c.AccessList, value.AccessList, options);
        writer.WritePropertyName(YParityName);
        c.UInt256.Write(writer, value.YParity, options);
        writer.WritePropertyName(ChainIdName);
        c.ULong.Write(writer, value.ChainId, options);
        writer.WritePropertyName(NonceName);
        c.ULong.Write(writer, value.Nonce, options);
        writer.WritePropertyName(ToName);
        WriteReference(writer, c.Address, value.To, options);
        writer.WritePropertyName(FromName);
        WriteReference(writer, c.Address, value.From, options);
        writer.WritePropertyName(ValueName);
        c.UInt256.Write(writer, value.Value, options);

        writer.WritePropertyName(InputName);
        WriteReference(writer, StrictHex, value.Input, options);
        writer.WritePropertyName(GasPriceName);
        c.UInt256.Write(writer, value.GasPrice, options);
        writer.WritePropertyName(VName);
        c.UInt256.Write(writer, value.V, options);
        writer.WritePropertyName(RName);
        SignatureWord.Write(writer, value.R, options);
        writer.WritePropertyName(SName);
        SignatureWord.Write(writer, value.S, options);
        writer.WritePropertyName(HashName);
        WriteReference(writer, c.Hash, value.Hash, options);
        writer.WritePropertyName(TransactionIndexName);
        c.Long.Write(writer, value.TransactionIndex, options);
        writer.WritePropertyName(BlockHashName);
        WriteReference(writer, c.Hash, value.BlockHash, options);
        writer.WritePropertyName(BlockNumberName);
        c.ULong.Write(writer, value.BlockNumber, options);
        writer.WritePropertyName(BlockTimestampName);
        c.ULong.Write(writer, value.BlockTimestamp, options);
        writer.WritePropertyName(GasName);
        c.ULong.Write(writer, value.Gas, options);
        writer.WriteEndObject();
    }

    // Reference-typed converters are not handed null by the serializer, so the writer writes it itself.
    private static void WriteReference<T>(Utf8JsonWriter writer, JsonConverter<T> converter, T? value, JsonSerializerOptions options) where T : class
    {
        if (value is null) writer.WriteNullValue();
        else converter.Write(writer, value, options);
    }

    private Converters GetConverters(JsonSerializerOptions options)
    {
        Converters? c = Volatile.Read(ref _converters);
        if (c is not null && ReferenceEquals(c.Options, options)) return c;
        c = new Converters(options);
        Volatile.Write(ref _converters, c);
        return c;
    }

    private sealed class Converters(JsonSerializerOptions options)
    {
        public readonly JsonSerializerOptions Options = options;
        public readonly JsonConverter<TxType?> TxType = (JsonConverter<TxType?>)options.GetConverter(typeof(TxType?));
        public readonly JsonConverter<UInt256?> UInt256 = (JsonConverter<UInt256?>)options.GetConverter(typeof(UInt256?));
        public readonly JsonConverter<ulong?> ULong = (JsonConverter<ulong?>)options.GetConverter(typeof(ulong?));
        public readonly JsonConverter<long?> Long = (JsonConverter<long?>)options.GetConverter(typeof(long?));
        public readonly JsonConverter<Address> Address = (JsonConverter<Address>)options.GetConverter(typeof(Address));
        public readonly JsonConverter<Hash256> Hash = (JsonConverter<Hash256>)options.GetConverter(typeof(Hash256));
        public readonly JsonConverter<AccessListForRpc> AccessList = (JsonConverter<AccessListForRpc>)options.GetConverter(typeof(AccessListForRpc));
    }
}
