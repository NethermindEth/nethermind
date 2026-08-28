// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// For sorting reasons - without storing full, large txs in memory
/// </summary>
public class LightTransaction : Transaction
{
    private readonly int _consensusEncodingSize;
    private StrongBox<BlobCellMask>? _blobCellMask;

    public LightTransaction(Transaction fullTx)
    {
        // Preserve the real type, or the delivered tx fails the announced-type check on the peer.
        Type = fullTx.Type;
        Hash = fullTx.Hash;
        SenderAddress = fullTx.SenderAddress;
        Nonce = fullTx.Nonce;
        Value = fullTx.Value;
        GasLimit = fullTx.GasLimit;
        GasPrice = fullTx.GasPrice; // means MaxPriorityFeePerGas
        DecodedMaxFeePerGas = fullTx.DecodedMaxFeePerGas;
        MaxFeePerBlobGas = fullTx.MaxFeePerBlobGas;
        BlobVersionedHashes = fullTx.BlobVersionedHashes;
        GasBottleneck = fullTx.GasBottleneck;
        Timestamp = fullTx.Timestamp;
        PoolIndex = fullTx.PoolIndex;
        ProofVersion = fullTx.GetProofVersion();
        // The pool holds this record, not the full tx, so its Removed event is what releases the payer's reservation.
        PayerAddress = fullTx.PayerAddress;
        // Without the keys the pool reads Nonce as an account nonce, and EIP-8250 nonce_seq is not one.
        NonceKeys = fullTx.NonceKeys;
        PersistedExpiryDeadline = FrameTxValidation.TryGetExpiryDeadline(fullTx, out ulong deadline) ? deadline : null;
        // Derived here or the cap never counts a blob-carrying frame tx: the pool holds this frameless
        // record, so the paymaster is no longer recoverable from the frame list once the full tx is gone.
        PersistedPaymaster = FrameTxValidation.GetPrefixPaymaster(fullTx);
        BlobCellMask = (fullTx.NetworkWrapper as ShardBlobNetworkWrapper)?.GetAvailableCellMask() ?? default;
        _consensusEncodingSize = fullTx.GetLength(shouldCountBlobs: false);
        _size = fullTx.GetLength();
    }

    /// <summary>Pre-EIP-8141 signature, kept so an out-of-tree <see cref="IBlobTxStorage"/> still binds.</summary>
    public LightTransaction(
        UInt256 timestamp,
        Address sender,
        ulong nonce,
        Hash256 hash,
        UInt256 value,
        ulong gasLimit,
        UInt256 gasPrice,
        UInt256 maxFeePerGas,
        UInt256 maxFeePerBlobGas,
        byte[][] blobVersionHashes,
        ulong poolIndex,
        int size,
        ProofVersion proofVersion)
        : this(timestamp, sender, nonce, hash, value, gasLimit, gasPrice, maxFeePerGas, maxFeePerBlobGas,
            blobVersionHashes, poolIndex, size, proofVersion, default, 0, TxType.Blob)
    {
    }

    // Declared in the order LightTxDecoder reads them: the optional trailing fields come last.
    public LightTransaction(
        UInt256 timestamp,
        Address sender,
        ulong nonce,
        Hash256 hash,
        UInt256 value,
        ulong gasLimit,
        UInt256 gasPrice,
        UInt256 maxFeePerGas,
        UInt256 maxFeePerBlobGas,
        byte[][] blobVersionHashes,
        ulong poolIndex,
        int size,
        ProofVersion proofVersion,
        BlobCellMask blobCellMask,
        int sparseBlobNetworkSize,
        TxType type,
        ulong? expiryDeadline = null,
        UInt256[]? nonceKeys = null)
    {
        Type = type;
        Hash = hash;
        SenderAddress = sender;
        Nonce = nonce;
        Value = value;
        GasLimit = gasLimit;
        GasPrice = gasPrice; // means MaxPriorityFeePerGas
        DecodedMaxFeePerGas = maxFeePerGas;
        MaxFeePerBlobGas = maxFeePerBlobGas;
        BlobVersionedHashes = blobVersionHashes;
        Timestamp = timestamp;
        PoolIndex = poolIndex;
        ProofVersion = proofVersion;
        BlobCellMask = blobCellMask;
        _consensusEncodingSize = sparseBlobNetworkSize;
        PersistedExpiryDeadline = expiryDeadline;
        NonceKeys = nonceKeys;
        _size = size;
    }

    public ProofVersion? ProofVersion { get; set; }

    /// <inheritdoc/>
    public override ulong? PersistedExpiryDeadline { get; }

    /// <inheritdoc/>
    public override Address? PersistedPaymaster { get; }

    /// <summary>
    /// Cell availability mask of the pooled sparse blob transaction.
    /// </summary>
    /// <remarks>
    /// Updated under the blob pool lock when cells are merged, but read without the lock on
    /// announcement paths. The value is published via an immutable box because a 16-byte struct
    /// write is not atomic and a torn mask would be recorded in per-peer announcement caches.
    /// </remarks>
    public BlobCellMask BlobCellMask
    {
        get => Volatile.Read(ref _blobCellMask)?.Value ?? default;
        private set => Volatile.Write(ref _blobCellMask, new StrongBox<BlobCellMask>(value));
    }

    internal void UpdateBlobPoolMetadata(BlobCellMask blobCellMask, int networkSize)
    {
        _size = networkSize;
        BlobCellMask = blobCellMask;
    }

    public override ProofVersion? GetProofVersion() => ProofVersion;

    public int GetConsensusEncodingSize() => _consensusEncodingSize;
}
