// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Taiko.TaikoSpec;

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
    /// Unzen sidecar field: carries the header difficulty (ZK gas used) through the Engine API
    /// newPayload direction. The driver populates this from blockValue returned by getPayload.
    /// </summary>
    public UInt256? HeaderDifficulty { get; set; }

    /// <summary>
    /// Non-serialised spec provider injected by <see cref="Rpc.TaikoEngineRpcModule"/> before the
    /// request is forwarded to the base handler. Used by <see cref="ApplyUnzenPinnedFields"/> to
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
    /// the inbound path (see <see cref="ApplyUnzenPinnedFields"/>).
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

    // Note: the base GetExecutionPayloadVersion override is intentionally absent.
    // Taiko ships exclusively over the V2 wire (TaikoGetPayloadV2Result + TaikoExecutionPayload
    // delivers, and IExecutionPayloadParams.ValidateParams above short-circuits to Success without
    // consulting GetExecutionPayloadVersion). With ApplyUnzenPinnedFields populating BlobGasUsed,
    // ExcessBlobGas and ParentBeaconBlockRoot on every Unzen block, the base override returns 3,
    // but that value is never read by the Taiko code path. Removing the previous (also 3-returning)
    // Taiko override eliminates dead code without changing observable behaviour.

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
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

            ApplyUnzenPinnedFields(header);
            return new Block(header, Array.Empty<Transaction>(), Array.Empty<BlockHeader>());
        }

        Result<Block> result = base.TryGetBlock(totalDifficulty);
        if (result.IsSuccess)
        {
            Block block = result.Data;
            if (HeaderDifficulty is not null)
            {
                block.Header.Difficulty = HeaderDifficulty.Value;
            }
            ApplyUnzenPinnedFields(block.Header);
        }
        return result;
    }

    /// <summary>
    /// Normalizes the four header fields a V2 Engine API payload cannot carry
    /// (<see cref="BlockHeader.BlobGasUsed"/>, <see cref="BlockHeader.ExcessBlobGas"/>,
    /// <see cref="BlockHeader.ParentBeaconBlockRoot"/>, <c>RequestsHash</c>) to their canonical
    /// Unzen values, or to <c>null</c> pre-Unzen, so the reconstructed header hash matches what
    /// the producer originally sealed.
    /// </summary>
    /// <remarks>
    /// The assignment is unconditional rather than null-coalescing: a strict V2 driver (Rust)
    /// omits these fields entirely while a Go driver round-trips them, and both must reduce to
    /// the same header. This mirrors alethia-reth's <c>TaikoEngineValidator::convert_payload_to_block</c>
    /// (<c>is_unzen_active.then_some(..)</c>). Gating on Unzen rather than the underlying
    /// EIP-4788 / EIP-4844 / EIP-7685 timestamps also matches alethia-reth. The spec provider is
    /// attached by <see cref="Rpc.TaikoEngineRpcModule"/> before the request reaches the base handler.
    /// </remarks>
    private void ApplyUnzenPinnedFields(BlockHeader header)
    {
        // Fail loudly rather than silently skip the pinning. A null _specProvider here means
        // a caller bypassed Rpc.TaikoEngineRpcModule.engine_newPayloadV{1,2} (which calls
        // AttachSpecProvider before delegating to base). Skipping the pinning would produce
        // a block-hash mismatch on Unzen blocks with no obvious diagnostic.
        if (_specProvider is null)
        {
            throw new InvalidOperationException(
                $"{nameof(TaikoExecutionPayload)}.{nameof(AttachSpecProvider)} must be called before {nameof(TryGetBlock)}.");
        }

        bool isUnzen = _specProvider.GetSpec(new ForkActivation(header.Number, header.Timestamp))
            is ITaikoReleaseSpec { IsUnzenEnabled: true };

        // Force the four header fields the V2 payload cannot carry to their canonical Unzen
        // values (and back to null pre-Unzen), regardless of what the inbound payload held.
        // Unconditional assignment keeps the reconstructed hash identical to alethia-reth's
        // TaikoEngineValidator::convert_payload_to_block whether the driver sends these fields
        // (Go) or omits them on the strict V2 wire (Rust).
        header.BlobGasUsed = isUnzen ? 0UL : null;
        header.ExcessBlobGas = isUnzen ? 0UL : null;
        header.ParentBeaconBlockRoot = isUnzen ? Keccak.Zero : null;
        header.RequestsHash = isUnzen ? ExecutionRequestExtensions.EmptyRequestsHash : null;
    }
}
