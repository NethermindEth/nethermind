// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayload</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayload
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
        ExcessDataGas = block.ExcessDataGas;

        SetTransactions(block.Transactions);
    }

    public UInt256 BaseFeePerGas { get; set; }

    public Keccak BlockHash { get; set; } = Keccak.Zero;

    public long BlockNumber { get; set; }

    public byte[] ExtraData { get; set; } = Array.Empty<byte>();

    public Address FeeRecipient { get; set; } = Address.Zero;

    public long GasLimit { get; set; }

    public long GasUsed { get; set; }

    public Bloom LogsBloom { get; set; } = Bloom.Empty;

    public Keccak ParentHash { get; set; } = Keccak.Zero;

    public Keccak PrevRandao { get; set; } = Keccak.Zero;

    public Keccak ReceiptsRoot { get; set; } = Keccak.Zero;

    public Keccak StateRoot { get; set; } = Keccak.Zero;

    public ulong Timestamp { get; set; }

    /// <summary>
    /// Gets or sets an array of RLP-encoded transaction where each item is a byte list (data)
    /// representing <c>TransactionType || TransactionPayload</c> or <c>LegacyTransaction</c> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-2718">EIP-2718</see>.
    /// </summary>
    public byte[][] Transactions { get; set; } = Array.Empty<byte[]>();

    /// <summary>
    /// Gets or sets a collection of <see cref="Withdrawal"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    public IEnumerable<Withdrawal>? Withdrawals { get; set; }

    [JsonConverter(typeof(NullableUInt256Converter))]
    public UInt256? ExcessDataGas { get; set; }

    /// <summary>
    /// Creates the execution block from payload.
    /// </summary>
    /// <param name="block">When this method returns, contains the execution block.</param>
    /// <param name="totalDifficulty">A total difficulty of the block.</param>
    /// <returns><c>true</c> if block created successfully; otherise, <c>false</c>.</returns>
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
                TxRoot = new TxTrie(transactions).RootHash,
                WithdrawalsRoot = Withdrawals is null ? null : new WithdrawalTrie(Withdrawals).RootHash,
                ExcessDataGas = ExcessDataGas,
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

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="Transactions"/>.
    /// </summary>
    /// <returns>An RLP-decoded array of <see cref="Transaction"/>.</returns>
    public Transaction[] GetTransactions() => Transactions
        .Select(t => Rlp.Decode<Transaction>(t, RlpBehaviors.SkipTypedWrapping))
        .ToArray();

    /// <summary>
    /// RLP-encodes and sets the transactions specified to <see cref="Transactions"/>.
    /// </summary>
    /// <param name="transactions">An array of transactions to encode.</param>
    public void SetTransactions(params Transaction[] transactions) => Transactions = transactions
        .Select(t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
        .ToArray();

    public override string ToString() => $"{BlockNumber} ({BlockHash})";
}
