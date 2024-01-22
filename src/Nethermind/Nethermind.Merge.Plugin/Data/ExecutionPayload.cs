// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayload</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayload : IForkValidator, IExecutionPayloadParams
{
    public ExecutionPayload() { } // Needed for tests

    public ExecutionPayload(Block block)
    {
        BlockHash = block.Hash!;
        ParentHash = block.ParentHash!;
        FeeRecipient = block.Beneficiary!;
        StateRoot = block.StateRoot!;
        BlockNumber = block.Number;
        GasLimit = block.GasLimit;
        GasUsed = block.GasUsed;
        ReceiptsRoot = block.ReceiptsRoot!;
        LogsBloom = block.Bloom!;
        PrevRandao = block.MixHash ?? Keccak.Zero;
        ExtraData = block.ExtraData!;
        Timestamp = block.Timestamp;
        BaseFeePerGas = block.BaseFeePerGas;
        Withdrawals = block.Withdrawals;

        SetTransactions(block.Transactions);
    }

    public UInt256 BaseFeePerGas { get; set; }

    public Hash256 BlockHash { get; set; } = Keccak.Zero;

    public long BlockNumber { get; set; }

    public byte[] ExtraData { get; set; } = Array.Empty<byte>();

    public Address FeeRecipient { get; set; } = Address.Zero;

    public long GasLimit { get; set; }

    public long GasUsed { get; set; }

    public Bloom LogsBloom { get; set; } = Bloom.Empty;

    public Hash256 ParentHash { get; set; } = Keccak.Zero;

    public Hash256 PrevRandao { get; set; } = Keccak.Zero;

    public Hash256 ReceiptsRoot { get; set; } = Keccak.Zero;

    public Hash256 StateRoot { get; set; } = Keccak.Zero;

    public ulong Timestamp { get; set; }

    private byte[][] _encodedTransactions = Array.Empty<byte[]>();

    /// <summary>
    /// Gets or sets an array of RLP-encoded transaction where each item is a byte list (data)
    /// representing <c>TransactionType || TransactionPayload</c> or <c>LegacyTransaction</c> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-2718">EIP-2718</see>.
    /// </summary>
    public byte[][] Transactions
    {
        get { return _encodedTransactions; }
        set
        {
            _encodedTransactions = value;
            _transactions = null;
        }
    }

    /// <summary>
    /// Gets or sets a collection of <see cref="Withdrawal"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    public Withdrawal[]? Withdrawals { get; set; }


    /// <summary>
    /// Gets or sets <see cref="Block.BlobGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? BlobGasUsed { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessBlobGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? ExcessBlobGas { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ParentBeaconBlockRoot"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4788">EIP-4788</see>.
    /// </summary>
    [JsonIgnore]
    public Hash256? ParentBeaconBlockRoot { get; set; }

    /// <summary>
    /// Creates the execution block from payload.
    /// </summary>
    /// <param name="block">When this method returns, contains the execution block.</param>
    /// <param name="totalDifficulty">A total difficulty of the block.</param>
    /// <returns><c>true</c> if block created successfully; otherwise, <c>false</c>.</returns>
    public virtual bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        try
        {
            var transactions = GetTransactions();
            var header = new BlockHeader(
                ParentHash,
                Keccak.OfAnEmptySequenceRlp,
                FeeRecipient,
                UInt256.Zero,
                BlockNumber,
                GasLimit,
                Timestamp,
                ExtraData)
            {
                Hash = BlockHash,
                ReceiptsRoot = ReceiptsRoot,
                StateRoot = StateRoot,
                Bloom = LogsBloom,
                GasUsed = GasUsed,
                BaseFeePerGas = BaseFeePerGas,
                Nonce = 0,
                MixHash = PrevRandao,
                Author = FeeRecipient,
                IsPostMerge = true,
                TotalDifficulty = totalDifficulty,
                TxRoot = TxTrie.CalculateRoot(transactions),
                WithdrawalsRoot = Withdrawals is null ? null : new WithdrawalTrie(Withdrawals).RootHash,
            };

            block = new(header, transactions, Array.Empty<BlockHeader>(), Withdrawals);

            return true;
        }
        catch (Exception)
        {
            block = null;
            return false;
        }
    }

    private Transaction[]? _transactions = null;

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="Transactions"/>.
    /// </summary>
    /// <returns>An RLP-decoded array of <see cref="Transaction"/>.</returns>
    public Transaction[] GetTransactions() => _transactions ??= Transactions
        .Select((t, i) =>
        {
            try
            {
                return Rlp.Decode<Transaction>(t, RlpBehaviors.SkipTypedWrapping);
            }
            catch (RlpException e)
            {
                throw new RlpException($"Transaction {i} is not valid", e);
            }
        }).ToArray();

    /// <summary>
    /// RLP-encodes and sets the transactions specified to <see cref="Transactions"/>.
    /// </summary>
    /// <param name="transactions">An array of transactions to encode.</param>
    public void SetTransactions(params Transaction[] transactions)
    {
        Transactions = transactions
            .Select(t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
            .ToArray();
        _transactions = transactions;
    }

    public override string ToString() => $"{BlockNumber} ({BlockHash.ToShortString()})";

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => this;

    public virtual ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        if (spec.IsEip4844Enabled)
        {
            error = "ExecutionPayloadV3 expected";
            return ValidationResult.Fail;
        }

        int actualVersion = this switch
        {
            { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
            { Withdrawals: not null } => 2,
            _ => 1
        };

        error = actualVersion switch
        {
            1 when spec.WithdrawalsEnabled => "ExecutionPayloadV2 expected",
            > 1 when !spec.WithdrawalsEnabled => "ExecutionPayloadV1 expected",
            _ => actualVersion > version ? $"ExecutionPayloadV{version} expected" : null
        };

        return error is null ? ValidationResult.Success : ValidationResult.Fail;
    }

    public virtual bool ValidateFork(ISpecProvider specProvider) =>
        !specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;
}
