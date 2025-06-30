// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.Facade.Eth;

public class BlockForRpc
{
    private static readonly BlockDecoder _blockDecoder = new();
    private readonly bool _isAuRaBlock;

    public BlockForRpc() { }

    [SkipLocalsInit]
    public BlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false)
    {
        _isAuRaBlock = block.Header.AuRaSignature is not null;
        Difficulty = block.Difficulty;
        ExtraData = block.ExtraData;
        GasLimit = block.GasLimit;
        GasUsed = block.GasUsed;
        Hash = block.Hash;
        LogsBloom = block.Bloom;
        Miner = block.Beneficiary;
        if (!_isAuRaBlock)
        {
            MixHash = block.MixHash;
            Nonce = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(Nonce, block.Nonce);
        }
        else
        {
            Author = block.Author;
            Step = block.Header.AuRaStep;
            Signature = block.Header.AuRaSignature;
        }

        if (specProvider is not null)
        {
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            if (spec.IsEip1559Enabled)
            {
                BaseFeePerGas = block.Header.BaseFeePerGas;
            }

            if (spec.IsEip4844Enabled)
            {
                BlobGasUsed = block.Header.BlobGasUsed;
                ExcessBlobGas = block.Header.ExcessBlobGas;
            }

            if (spec.IsEip4788Enabled)
            {
                ParentBeaconBlockRoot = block.ParentBeaconBlockRoot;
            }
        }

        Number = block.Number;
        ParentHash = block.ParentHash;
        ReceiptsRoot = block.ReceiptsRoot;
        Sha3Uncles = block.UnclesHash;
        Size = _blockDecoder.GetLength(block, RlpBehaviors.None);
        StateRoot = block.StateRoot;
        Timestamp = block.Timestamp;
        TotalDifficulty = block.TotalDifficulty ?? 0;
        if (!skipTxs)
        {
            Transactions = includeFullTransactionData
                    ? GetTransactionsForRpc(block, specProvider.ChainId)
                    : GetTransactionHashes(block.Transactions);
        }
        TransactionsRoot = block.TxRoot;
        Uncles = GetUnclesHashes(block.Uncles);
        Withdrawals = block.Withdrawals;
        WithdrawalsRoot = block.Header.WithdrawalsRoot;
        RequestsHash = block.Header.RequestsHash;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Address? Author { get; set; }
    public UInt256 Difficulty { get; set; }
    public byte[] ExtraData { get; set; }
    public long GasLimit { get; set; }
    public long GasUsed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256 Hash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Bloom LogsBloom { get; set; }
    public Address Miner { get; set; }
    public Hash256 MixHash { get; set; }

    public bool ShouldSerializeMixHash() => !_isAuRaBlock && MixHash is not null;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[] Nonce { get; set; }

    public bool ShouldSerializeNonce() => !_isAuRaBlock;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? Number { get; set; }
    public Hash256 ParentHash { get; set; }
    public Hash256 ReceiptsRoot { get; set; }
    public Hash256 Sha3Uncles { get; set; }
    public byte[] Signature { get; set; }
    public bool ShouldSerializeSignature() => _isAuRaBlock;
    public long Size { get; set; }
    public Hash256 StateRoot { get; set; }
    [JsonConverter(typeof(NullableRawLongConverter))]
    public long? Step { get; set; }
    public bool ShouldSerializeStep() => _isAuRaBlock;
    public UInt256 Timestamp { get; set; }

    public UInt256? BaseFeePerGas { get; set; }
    public object[] Transactions { get; set; }
    public Hash256 TransactionsRoot { get; set; }
    public Hash256[] Uncles { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Withdrawal[]? Withdrawals { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? WithdrawalsRoot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? BlobGasUsed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? ExcessBlobGas { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? ParentBeaconBlockRoot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? RequestsHash { get; set; }

    private static object[] GetTransactionHashes(Transaction[] transactions)
    {
        if (transactions.Length == 0) return Array.Empty<Hash256>();

        Hash256[] hashes = new Hash256[transactions.Length];
        for (var i = 0; i < transactions.Length; i++)
        {
            hashes[i] = transactions[i].Hash;
        }
        return hashes;
    }

    private static object[] GetTransactionsForRpc(Block block, ulong chainId)
    {
        Transaction[] transactions = block.Transactions;
        if (transactions.Length == 0) return Array.Empty<TransactionForRpc>();

        TransactionForRpc[] txs = new TransactionForRpc[transactions.Length];
        for (var i = 0; i < transactions.Length; i++)
        {
            txs[i] = TransactionForRpc.FromTransaction(transactions[i], block.Hash, block.Number, i, block.BaseFeePerGas, chainId);
        }
        return txs;
    }

    private static Hash256[] GetUnclesHashes(BlockHeader[] headers)
    {
        if (headers.Length == 0) return Array.Empty<Hash256>();

        Hash256[] hashes = new Hash256[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            hashes[i] = headers[i].Hash;
        }
        return hashes;
    }
}
