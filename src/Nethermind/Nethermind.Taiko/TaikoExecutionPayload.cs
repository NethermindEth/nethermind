// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Taiko;

public class TaikoExecutionPayload : ExecutionPayload
{
    public Hash256? WithdrawalsHash { get; set; } = null;
    public Hash256? TxHash { get; set; } = null;

    /// <summary>
    /// L1Sload precompile storage mappings.
    /// Contains [(address, key, block) -> value] mappings for L1SLOAD calls.
    /// </summary>
    public L1StorageMapping[]? L1StorageMappings { get; set; }

    public new byte[][]? Transactions
    {
        get => _encodedTransactions is [] ? null : _encodedTransactions;
        set
        {
            _encodedTransactions = value ?? [];
            _transactions = null;
        }
    }

    protected override int GetExecutionPayloadVersion() => this switch
    {
        { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
        { WithdrawalsHash: not null } or { Withdrawals: not null } => 2, // modified
        _ => 1
    };

    public override BlockDecodingResult TryGetBlock(UInt256? totalDifficulty = null)
    {
        if (Withdrawals is null && Transactions is null)
        {
            BlockHeader header = new BlockHeader(
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
                TxRoot = TxHash,
                WithdrawalsRoot = WithdrawalsHash,
            };

            return new BlockDecodingResult(new Block(header, Array.Empty<Transaction>(), Array.Empty<BlockHeader>()));
        }

        if (L1StorageMappings is not null)
        {
            SetL1StorageData();
        }

        return base.TryGetBlock(totalDifficulty);
    }

    /// <summary>
    /// Sets L1 storage data to be used by the L1Sload precompile.
    /// </summary>
    private void SetL1StorageData()
    {
        var storageProvider = L1SloadPrecompile.L1StorageProvider as SurgeL1StorageProvider;
        storageProvider?.SetBlockStorageData(L1StorageMappings);
    }
}

/// <summary>
/// Represents L1 storage mapping for L1SLOAD precompile.
/// </summary>
public class L1StorageMapping
{
    public required Address ContractAddress { get; set; }
    public UInt256 StorageKey { get; set; }
    public UInt256 BlockNumber { get; set; }
    public UInt256 StorageValue { get; set; }
}
