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
    /// Non-serialised spec provider injected by <see cref="Rpc.TaikoEngineRpcModule"/> before the
    /// request is forwarded to the base handler. Used by <see cref="ApplyUzenPinnedFields"/> to
    /// determine which EIPs are active at the block's timestamp so header fields that V2 payloads
    /// cannot carry (ParentBeaconBlockRoot, RequestsHash) can be restored to their canonical
    /// zero/empty values for Taiko L2.
    /// </summary>
    private ISpecProvider? _specProvider;

    /// <summary>
    /// Attaches the spec provider so that <see cref="TryGetBlock"/> can determine the active
    /// EIPs at the block's timestamp.  Must be called before <see cref="TryGetBlock"/> is
    /// invoked.  Not serialised; this reference is only valid within a single Engine API request.
    /// </summary>
    internal void AttachSpecProvider(ISpecProvider specProvider) => _specProvider = specProvider;

    /// <summary>
    /// Creates a <see cref="TaikoExecutionPayload"/> from a <see cref="Block"/>.
    /// Also copies the EIP-4844 header fields (<see cref="Block.BlobGasUsed"/> and
    /// <see cref="Block.ExcessBlobGas"/>) that the base
    /// <see cref="ExecutionPayload.Create{TExecutionPayload}"/> does not copy, so the Go
    /// driver sees the same values Nethermind used when hashing the block.
    /// <see cref="ExecutionPayload.ParentBeaconBlockRoot"/> is <c>[JsonIgnore]</c> on the
    /// base type, so it never reaches the wire and is restored from the spec provider on
    /// the inbound path (see <see cref="ApplyUzenPinnedFields"/>).
    /// </summary>
    public new static TaikoExecutionPayload Create(Block block)
    {
        TaikoExecutionPayload payload = Create<TaikoExecutionPayload>(block);
        payload.BlobGasUsed = block.BlobGasUsed;
        payload.ExcessBlobGas = block.ExcessBlobGas;
        return payload;
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
    /// Restores header fields that the V2 Engine API payload cannot carry through JSON so
    /// that the reconstructed header hash matches the one Nethermind produced originally.
    /// <list type="bullet">
    ///   <item><description><see cref="BlobGasUsed"/> / <see cref="ExcessBlobGas"/> are
    ///     serialised (<c>JsonIgnoreCondition.WhenWritingNull</c>) so the payload carries
    ///     them when present; we only copy them across when non-null.</description></item>
    ///   <item><description><see cref="ExecutionPayload.ParentBeaconBlockRoot"/> is
    ///     <c>[JsonIgnore]</c> on the base type and therefore always arrives <c>null</c>.
    ///     Pin it to <see cref="Keccak.Zero"/> whenever EIP-4788 is active at the block's
    ///     timestamp (Taiko L2 has no beacon chain root).</description></item>
    ///   <item><description><c>RequestsHash</c> is not a payload field at all. Pin it to
    ///     <see cref="ExecutionRequestExtensions.EmptyRequestsHash"/> whenever EIP-7685 is
    ///     active (Taiko L2 has no execution-layer requests).</description></item>
    /// </list>
    /// The spec provider is attached by <see cref="Rpc.TaikoEngineRpcModule"/> before the
    /// request is dispatched to the base handler.
    /// </summary>
    private void ApplyUzenPinnedFields(BlockHeader header)
    {
        if (BlobGasUsed is not null) header.BlobGasUsed ??= BlobGasUsed.Value;
        if (ExcessBlobGas is not null) header.ExcessBlobGas ??= ExcessBlobGas.Value;

        IReleaseSpec? spec = _specProvider?.GetSpec(new ForkActivation(header.Number, header.Timestamp));

        if (spec?.IsEip4788Enabled == true)
        {
            header.ParentBeaconBlockRoot ??= Keccak.Zero;
        }

        if (spec?.RequestsEnabled == true)
        {
            header.RequestsHash ??= ExecutionRequestExtensions.EmptyRequestsHash;
        }
    }
}
