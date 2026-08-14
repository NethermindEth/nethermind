// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// JSON-RPC view of an EIP-8141 frame transaction (TxType 0x06): the EIP-1559 fee fields plus the
/// frame list and the hoisted signature list. Without this converter frame txs would serialize as
/// a generic transaction, dropping their frame-specific fields.
/// </summary>
public class FrameTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<FrameTransactionForRpc>
{
    public new static TxType TxType => TxType.FrameTx;

    public override TxType? Type => TxType;

    [JsonDiscriminator]
    public FrameForRpc[]? Frames { get; set; }

    public FrameSignatureForRpc[]? Signatures { get; set; }

    /// <summary><c>max_fee_per_blob_gas</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerBlobGas { get; set; }

    /// <summary><c>blob_versioned_hashes</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[][]? BlobVersionedHashes { get; set; }

    public RecentRootReferenceForRpc[]? RecentRootReferences { get; set; }

    [JsonConstructor]
    public FrameTransactionForRpc() { }

    public FrameTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        Frames = FrameForRpc.FromFrames(transaction.Frames);
        Signatures = FrameSignatureForRpc.FromSignatures(transaction.FrameSignatures);
        RecentRootReferences = RecentRootReferenceForRpc.FromReferences(transaction.RecentRootReferences);

        // Covered by the sig hash, so always reported: a consumer must be able to rebuild the payload.
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;
        tx.Frames = FrameForRpc.ToFrames(Frames);
        tx.FrameSignatures = FrameSignatureForRpc.ToSignatures(Signatures);
        tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
        tx.BlobVersionedHashes = BlobVersionedHashes;
        tx.RecentRootReferences = RecentRootReferenceForRpc.ToReferences(RecentRootReferences);
        return tx;
    }

    public new static FrameTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
