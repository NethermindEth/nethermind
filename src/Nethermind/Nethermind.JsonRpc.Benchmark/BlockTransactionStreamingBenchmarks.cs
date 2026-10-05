// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Measures writing full-block transactions straight from the stored <see cref="Transaction"/> through one reused RPC object,
/// against materializing a <see cref="TransactionForRpc"/> per transaction as the RPC module does today.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class BlockTransactionStreamingBenchmarks
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private readonly RpcResultSerializationBenchmarks _rpc = new();
    private readonly StreamingTransactionsConverter _streaming = new();
    private JsonSerializerOptions _options = null!;
    private JsonSerializerOptions _streamingOptions = null!;
    private Block _block = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rpc.Setup();
        _block = _rpc.Block;
        _options = EthereumJsonSerializer.JsonOptions;
        _streamingOptions = new JsonSerializerOptions(_options);
        _streamingOptions.Converters.Insert(0, _streaming);
        _streaming.Block = _block;

        Check("build + serialize", () => { BuildAndSerializeMaterialized(); return _buffer.WrittenSpan.ToArray(); }, () => { BuildAndSerializeStreamed(); return _buffer.WrittenSpan.ToArray(); });
        Check("eth_getBlockByNumber", () => { RpcMaterialized(); return _rpc.Written.ToArray(); }, () => { RpcStreamed(); return _rpc.Written.ToArray(); });
    }

    [GlobalCleanup]
    public void Cleanup() => _rpc.Cleanup();

    [BenchmarkCategory("build + serialize full block"), Benchmark(Baseline = true, Description = "TransactionForRpc per tx")]
    public int BuildAndSerializeMaterialized() => Serialize(new BlockForRpc(_block, includeFullTransactionData: true, MainnetSpecProvider.Instance), _options);

    [BenchmarkCategory("build + serialize full block"), Benchmark(Description = "streamed, one reused object")]
    public int BuildAndSerializeStreamed() => Serialize(new BlockForRpc(_block, includeFullTransactionData: false, MainnetSpecProvider.Instance), _streamingOptions);

    [BenchmarkCategory("eth_getBlockByNumber full"), Benchmark(Baseline = true, Description = "TransactionForRpc per tx")]
    public int RpcMaterialized() => _rpc.RpcWith(true, _options);

    [BenchmarkCategory("eth_getBlockByNumber full"), Benchmark(Description = "streamed, one reused object")]
    public int RpcStreamed() => _rpc.RpcWith(false, _streamingOptions);

    private int Serialize(BlockForRpc value, JsonSerializerOptions options)
    {
        _buffer.ResetWrittenCount();
        using (Utf8JsonWriter writer = new(_buffer, new JsonWriterOptions { SkipValidation = true, Encoder = options.Encoder }))
        {
            TypeInfoJsonSerializer.Serialize(writer, value, options);
        }

        return _buffer.WrittenCount;
    }

    private static void Check(string name, Func<byte[]> materialized, Func<byte[]> streamed)
    {
        byte[] expected = materialized();
        byte[] actual = streamed();
        if (!expected.AsSpan().SequenceEqual(actual) || expected.Length < 1000) throw new InvalidOperationException($"{name}: streamed output differs");
    }

    /// <summary>Stands in for the full-mode wrapper: ignores the hashes it is given and streams the block's transactions.</summary>
    private sealed class StreamingTransactionsConverter : JsonConverter<BlockTransactions>
    {
        private readonly EIP1559TransactionForRpc _reused = new();

        public Block Block { get; set; } = null!;

        public override BlockTransactions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, BlockTransactions value, JsonSerializerOptions options)
        {
            JsonConverter<TransactionForRpc> dispatch = (JsonConverter<TransactionForRpc>)TypeInfoJsonSerializer.GetTypeInfo<TransactionForRpc>(options).Converter;
            Transaction[] transactions = Block.Transactions;
            writer.WriteStartArray();
            for (int i = 0; i < transactions.Length; i++)
            {
                Populate(_reused, transactions[i], new TransactionForRpcContext(BlockchainIds.Mainnet, Block.Hash!, Block.Number, i, Block.Timestamp, Block.BaseFeePerGas));
                dispatch.Write(writer, _reused, options);
            }

            writer.WriteEndArray();
        }

        // Mirrors the TransactionForRpc, Legacy, AccessList and EIP1559 constructors in order, for EIP-1559 transactions only.
        private static void Populate(EIP1559TransactionForRpc rpc, Transaction tx, in TransactionForRpcContext extraData)
        {
            if (tx.Type != TxType.EIP1559) throw new NotSupportedException();

            rpc.Hash = tx.Hash;
            rpc.TransactionIndex = extraData.TxIndex;
            rpc.BlockHash = extraData.BlockHash;
            rpc.BlockNumber = extraData.BlockNumber;
            rpc.BlockTimestamp = extraData.BlockTimestamp;

            rpc.Nonce = tx.Nonce;
            rpc.To = tx.To;
            rpc.From = tx.SenderAddress;
            rpc.Gas = tx.GasLimit;
            rpc.Value = tx.Value;
            rpc.Input = tx.Data.AsArray();
            rpc.GasPrice = tx.GasPrice;
            Signature? signature = tx.Signature;
            if (signature is null)
            {
                rpc.R = UInt256.Zero;
                rpc.S = UInt256.Zero;
                rpc.V = 0;
                rpc.ChainId = tx.ChainId;
            }
            else
            {
                rpc.R = new UInt256(signature.R.Span, true);
                rpc.S = new UInt256(signature.S.Span, true);
                rpc.V = signature.V;
                rpc.ChainId = tx.ChainId ?? signature.ChainId;
            }

            rpc.AccessList = AccessListForRpc.FromAccessList(tx.AccessList);
            rpc.YParity = tx.Signature?.RecoveryId ?? 0;
            rpc.ChainId = tx.ChainId ?? extraData.ChainId ?? BlockchainIds.Mainnet;
            rpc.V = rpc.YParity ?? 0;

            rpc.MaxFeePerGas = tx.MaxFeePerGas;
            rpc.MaxPriorityFeePerGas = tx.MaxPriorityFeePerGas;
            rpc.GasPrice = extraData.BaseFee is not null ? tx.CalculateEffectiveGasPrice(eip1559Enabled: true, extraData.BaseFee.Value) : tx.MaxFeePerGas;
        }
    }
}
