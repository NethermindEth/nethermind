// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>Stand-in for generator output for <see cref="FilterLog"/>; see <see cref="GeneratedStyleTransactionWriter"/>.</summary>
internal sealed class GeneratedStyleFilterLogWriter : JsonConverter<FilterLog>
{
    private static readonly JsonEncodedText AddressName = JsonEncodedText.Encode("address");
    private static readonly JsonEncodedText BlockHashName = JsonEncodedText.Encode("blockHash");
    private static readonly JsonEncodedText BlockNumberName = JsonEncodedText.Encode("blockNumber");
    private static readonly JsonEncodedText BlockTimestampName = JsonEncodedText.Encode("blockTimestamp");
    private static readonly JsonEncodedText DataName = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText LogIndexName = JsonEncodedText.Encode("logIndex");
    private static readonly JsonEncodedText RemovedName = JsonEncodedText.Encode("removed");
    private static readonly JsonEncodedText TopicsName = JsonEncodedText.Encode("topics");
    private static readonly JsonEncodedText TransactionHashName = JsonEncodedText.Encode("transactionHash");
    private static readonly JsonEncodedText TransactionIndexName = JsonEncodedText.Encode("transactionIndex");

    private ResolvedConverters? _converters;

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(FilterLog);

    public override FilterLog Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, FilterLog value, JsonSerializerOptions options)
    {
        ResolvedConverters c = ResolvedConverters.Get(ref _converters, options);

        writer.WriteStartObject();
        if (value.Address is { } address)
        {
            writer.WritePropertyName(AddressName);
            c.Address.Write(writer, address, options);
        }

        if (value.BlockHash is { } blockHash)
        {
            writer.WritePropertyName(BlockHashName);
            c.Hash.Write(writer, blockHash, options);
        }

        writer.WritePropertyName(BlockNumberName);
        c.ULong.Write(writer, value.BlockNumber, options);
        writer.WritePropertyName(BlockTimestampName);
        c.ULong.Write(writer, value.BlockTimestamp, options);
        if (value.Data is { } data)
        {
            writer.WritePropertyName(DataName);
            c.Bytes.Write(writer, data, options);
        }

        writer.WritePropertyName(LogIndexName);
        c.Long.Write(writer, value.LogIndex, options);
        writer.WritePropertyName(RemovedName);
        c.Bool.Write(writer, value.Removed, options);
        if (value.Topics is { } topics)
        {
            writer.WritePropertyName(TopicsName);
            c.Hashes.Write(writer, topics, options);
        }

        if (value.TransactionHash is { } transactionHash)
        {
            writer.WritePropertyName(TransactionHashName);
            c.Hash.Write(writer, transactionHash, options);
        }

        writer.WritePropertyName(TransactionIndexName);
        c.Long.Write(writer, value.TransactionIndex, options);
        writer.WriteEndObject();
    }
}

/// <summary>Stand-in for generator output for <see cref="ReceiptForRpc"/>; the logs keep their hand-written converter.</summary>
internal sealed class GeneratedStyleReceiptWriter : JsonConverter<ReceiptForRpc>
{
    private static readonly JsonEncodedText TransactionHashName = JsonEncodedText.Encode("transactionHash");
    private static readonly JsonEncodedText TransactionIndexName = JsonEncodedText.Encode("transactionIndex");
    private static readonly JsonEncodedText BlockHashName = JsonEncodedText.Encode("blockHash");
    private static readonly JsonEncodedText BlockNumberName = JsonEncodedText.Encode("blockNumber");
    private static readonly JsonEncodedText CumulativeGasUsedName = JsonEncodedText.Encode("cumulativeGasUsed");
    private static readonly JsonEncodedText GasUsedName = JsonEncodedText.Encode("gasUsed");
    private static readonly JsonEncodedText BlockGasUsedName = JsonEncodedText.Encode("blockGasUsed");
    private static readonly JsonEncodedText ExecutionGasUsedName = JsonEncodedText.Encode("executionGasUsed");
    private static readonly JsonEncodedText StorageGasUsedName = JsonEncodedText.Encode("storageGasUsed");
    private static readonly JsonEncodedText BlobGasUsedName = JsonEncodedText.Encode("blobGasUsed");
    private static readonly JsonEncodedText BlobGasPriceName = JsonEncodedText.Encode("blobGasPrice");
    private static readonly JsonEncodedText EffectiveGasPriceName = JsonEncodedText.Encode("effectiveGasPrice");
    private static readonly JsonEncodedText FromName = JsonEncodedText.Encode("from");
    private static readonly JsonEncodedText ToName = JsonEncodedText.Encode("to");
    private static readonly JsonEncodedText ContractAddressName = JsonEncodedText.Encode("contractAddress");
    private static readonly JsonEncodedText LogsName = JsonEncodedText.Encode("logs");
    private static readonly JsonEncodedText LogsBloomName = JsonEncodedText.Encode("logsBloom");
    private static readonly JsonEncodedText RootName = JsonEncodedText.Encode("root");
    private static readonly JsonEncodedText StatusName = JsonEncodedText.Encode("status");
    private static readonly JsonEncodedText PayerName = JsonEncodedText.Encode("payer");
    private static readonly JsonEncodedText FrameReceiptsName = JsonEncodedText.Encode("frameReceipts");
    private static readonly JsonEncodedText TypeName = JsonEncodedText.Encode("type");

    private static readonly LogsForRpcConverter Logs = new();

    private ResolvedConverters? _converters;

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(ReceiptForRpc);

    public override ReceiptForRpc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ReceiptForRpc value, JsonSerializerOptions options)
    {
        ResolvedConverters c = ResolvedConverters.Get(ref _converters, options);

        writer.WriteStartObject();
        if (value.TransactionHash is { } transactionHash)
        {
            writer.WritePropertyName(TransactionHashName);
            c.Hash.Write(writer, transactionHash, options);
        }

        writer.WritePropertyName(TransactionIndexName);
        c.Long.Write(writer, value.TransactionIndex, options);
        if (value.BlockHash is { } blockHash)
        {
            writer.WritePropertyName(BlockHashName);
            c.Hash.Write(writer, blockHash, options);
        }

        writer.WritePropertyName(BlockNumberName);
        c.ULong.Write(writer, value.BlockNumber, options);
        writer.WritePropertyName(CumulativeGasUsedName);
        c.ULong.Write(writer, value.CumulativeGasUsed, options);
        writer.WritePropertyName(GasUsedName);
        c.ULong.Write(writer, value.GasUsed, options);
        WriteUnlessDefault(writer, BlockGasUsedName, c.ULong, value.BlockGasUsed, options);
        WriteUnlessDefault(writer, ExecutionGasUsedName, c.ULong, value.ExecutionGasUsed, options);
        WriteUnlessDefault(writer, StorageGasUsedName, c.ULong, value.StorageGasUsed, options);
        if (value.BlobGasUsed is not null)
        {
            writer.WritePropertyName(BlobGasUsedName);
            c.NullableULong.Write(writer, value.BlobGasUsed, options);
        }

        if (value.BlobGasPrice is not null)
        {
            writer.WritePropertyName(BlobGasPriceName);
            c.NullableUInt256.Write(writer, value.BlobGasPrice, options);
        }

        if (value.EffectiveGasPrice is not null)
        {
            writer.WritePropertyName(EffectiveGasPriceName);
            c.NullableUInt256.Write(writer, value.EffectiveGasPrice, options);
        }

        if (value.From is { } from)
        {
            writer.WritePropertyName(FromName);
            c.Address.Write(writer, from, options);
        }

        writer.WritePropertyName(ToName);
        WriteReference(writer, c.Address, value.To, options);
        writer.WritePropertyName(ContractAddressName);
        WriteReference(writer, c.Address, value.ContractAddress, options);
        if (value.Logs is { } logs)
        {
            writer.WritePropertyName(LogsName);
            Logs.Write(writer, logs, options);
        }

        if (value.LogsBloom is { } logsBloom)
        {
            writer.WritePropertyName(LogsBloomName);
            c.Bloom.Write(writer, logsBloom, options);
        }

        if (value.Root is { } root)
        {
            writer.WritePropertyName(RootName);
            c.Hash.Write(writer, root, options);
        }

        if (value.Status is not null)
        {
            writer.WritePropertyName(StatusName);
            c.NullableLong.Write(writer, value.Status, options);
        }

        if (value.Payer is { } payer)
        {
            writer.WritePropertyName(PayerName);
            c.Address.Write(writer, payer, options);
        }

        if (value.FrameReceipts is { } frameReceipts)
        {
            // Types without generated writers stay on the metadata path.
            writer.WritePropertyName(FrameReceiptsName);
            TypeInfoJsonSerializer.Serialize(writer, frameReceipts, options);
        }

        writer.WritePropertyName(TypeName);
        c.TxType.Write(writer, value.Type, options);
        writer.WriteEndObject();
    }

    private static void WriteUnlessDefault(Utf8JsonWriter writer, JsonEncodedText name, JsonConverter<ulong> converter, ulong value, JsonSerializerOptions options)
    {
        if (value == 0) return;
        writer.WritePropertyName(name);
        converter.Write(writer, value, options);
    }

    private static void WriteReference<T>(Utf8JsonWriter writer, JsonConverter<T> converter, T? value, JsonSerializerOptions options) where T : class
    {
        if (value is null) writer.WriteNullValue();
        else converter.Write(writer, value, options);
    }
}

/// <summary>The converters the options resolve for the property types the stand-in writers use, cached per options instance.</summary>
internal sealed class ResolvedConverters(JsonSerializerOptions options)
{
    public readonly JsonSerializerOptions Options = options;
    public readonly JsonConverter<Address> Address = (JsonConverter<Address>)options.GetConverter(typeof(Address));
    public readonly JsonConverter<Hash256> Hash = (JsonConverter<Hash256>)options.GetConverter(typeof(Hash256));
    public readonly JsonConverter<Hash256[]> Hashes = (JsonConverter<Hash256[]>)options.GetConverter(typeof(Hash256[]));
    public readonly JsonConverter<ulong> ULong = (JsonConverter<ulong>)options.GetConverter(typeof(ulong));
    public readonly JsonConverter<ulong?> NullableULong = (JsonConverter<ulong?>)options.GetConverter(typeof(ulong?));
    public readonly JsonConverter<long> Long = (JsonConverter<long>)options.GetConverter(typeof(long));
    public readonly JsonConverter<long?> NullableLong = (JsonConverter<long?>)options.GetConverter(typeof(long?));
    public readonly JsonConverter<UInt256?> NullableUInt256 = (JsonConverter<UInt256?>)options.GetConverter(typeof(UInt256?));
    public readonly JsonConverter<bool> Bool = (JsonConverter<bool>)options.GetConverter(typeof(bool));
    public readonly JsonConverter<byte[]> Bytes = (JsonConverter<byte[]>)options.GetConverter(typeof(byte[]));
    public readonly JsonConverter<Bloom> Bloom = (JsonConverter<Bloom>)options.GetConverter(typeof(Bloom));
    public readonly JsonConverter<TxType> TxType = (JsonConverter<TxType>)options.GetConverter(typeof(TxType));

    public static ResolvedConverters Get(ref ResolvedConverters? cache, JsonSerializerOptions options)
    {
        ResolvedConverters? c = Volatile.Read(ref cache);
        if (c is not null && ReferenceEquals(c.Options, options)) return c;
        c = new ResolvedConverters(options);
        Volatile.Write(ref cache, c);
        return c;
    }
}
