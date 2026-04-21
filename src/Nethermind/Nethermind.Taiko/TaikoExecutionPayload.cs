// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Taiko;

public class TaikoExecutionPayload : ExecutionPayload, IExecutionPayloadParams, IExecutionPayloadFactory<TaikoExecutionPayload>
{
    /// <summary>
    /// Taiko always uses V2 payloads regardless of the EVM spec (Cancun/Prague/Osaka).
    /// The base ValidateFork would reject V2 payloads when EIP-4844 is active.
    /// </summary>
    public override bool ValidateFork(ISpecProvider specProvider) => true;

    /// <summary>
    /// Taiko always uses V2 engine API payloads. The base ValidateParams rejects V2 once
    /// IsEip4844Enabled, demanding V3. Skip that check entirely for Taiko.
    /// </summary>
    Nethermind.Merge.Plugin.Data.ValidationResult IExecutionPayloadParams.ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        error = null;
        return Nethermind.Merge.Plugin.Data.ValidationResult.Success;
    }
    public Hash256? WithdrawalsHash { get; set; } = null;
    public Hash256? TxHash { get; set; } = null;

    /// <summary>
    /// Uzen sidecar field: carries the header difficulty (ZK gas used) through the Engine API
    /// newPayload direction. The driver populates this from blockValue returned by getPayload.
    /// </summary>
    public UInt256? HeaderDifficulty { get; set; }

    /// <summary>
    /// Creates a <see cref="TaikoExecutionPayload"/> from a <see cref="Block"/>.
    /// </summary>
    public new static TaikoExecutionPayload Create(Block block)
    {
        return Create<TaikoExecutionPayload>(block);
    }

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
            BlockHeader header = new(
                ParentHash,
                Keccak.OfAnEmptySequenceRlp,
                FeeRecipient,
                HeaderDifficulty ?? UInt256.Zero,
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

            ApplyUzenPinnedFields(header);
            return new BlockDecodingResult(new Block(header, Array.Empty<Transaction>(), Array.Empty<BlockHeader>()));
        }

        BlockDecodingResult result = base.TryGetBlock(totalDifficulty);
        if (result.Block is not null)
        {
            if (HeaderDifficulty is not null)
            {
                result.Block.Header.Difficulty = HeaderDifficulty.Value;
            }
            ApplyUzenPinnedFields(result.Block.Header);
        }
        return result;
    }

    /// <summary>
    /// V2 payloads don't carry Cancun/Prague header fields. For Uzen blocks these are
    /// pinned to known values, so we inject them when the payload didn't supply them.
    /// For pre-Uzen blocks these fields stay null and are not part of the header RLP.
    /// </summary>
    private static void ApplyUzenPinnedFields(BlockHeader header)
    {
        header.BlobGasUsed ??= 0;
        header.ExcessBlobGas ??= 0;
        header.ParentBeaconBlockRoot ??= Keccak.Zero;
        header.RequestsHash ??= ExecutionRequestExtensions.EmptyRequestsHash;
    }
}
