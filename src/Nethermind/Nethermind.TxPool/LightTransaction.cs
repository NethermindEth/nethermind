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
    private int _elidedNetworkEncodingSize;
    private StrongBox<BlobCellMask>? _blobCellMask;

    public LightTransaction(MempoolTransaction mempoolTx)
    {
        Transaction fullTx = mempoolTx.Transaction;
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
        MaxFee = fullTx.MaxFee;
        BlobVersionedHashes = fullTx.BlobVersionedHashes;
        Timestamp = fullTx.Timestamp;
        ProofVersion = mempoolTx.NetworkWrapper?.Version;
        // Without the keys the pool reads Nonce as an account nonce, and EIP-8250 nonce_seq is not one.
        NonceKeys = fullTx.NonceKeys;
        PersistedExpiryDeadline = FrameTxValidation.TryGetExpiryDeadline(fullTx, out ulong deadline) ? deadline : null;
        // Derived here or the cap never counts a blob-carrying frame tx: the pool holds this frameless
        // record, so the paymaster is no longer recoverable from the frame list once the full tx is gone.
        PersistedPaymaster = PendingPaymasterCache.KeyFor(fullTx);
        BlobCellMask = mempoolTx.NetworkWrapper?.GetAvailableCellMask() ?? default;
        _consensusEncodingSize = fullTx.GetLength();
        _size = _consensusEncodingSize;
        NetworkLength = mempoolTx.GetLength();
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
        int size,
        ProofVersion proofVersion)
        : this(timestamp, sender, nonce, hash, value, gasLimit, gasPrice, maxFeePerGas, maxFeePerBlobGas,
            blobVersionHashes, size, proofVersion, default, 0, TxType.Blob)
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
        int size,
        ProofVersion proofVersion,
        BlobCellMask blobCellMask,
        int sparseBlobNetworkSize,
        TxType type,
        ulong? expiryDeadline = null,
        UInt256[]? nonceKeys = null,
        Address? paymaster = null,
        UInt256 maxFee = default)
        : this(timestamp, sender, nonce, hash, value, gasLimit, gasPrice, maxFeePerGas, maxFeePerBlobGas,
            blobVersionHashes, size, proofVersion, blobCellMask, sparseBlobNetworkSize, 0,
            type, expiryDeadline, nonceKeys, paymaster, maxFee)
    {
    }

    internal LightTransaction(
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
        int size,
        ProofVersion proofVersion,
        BlobCellMask blobCellMask,
        int consensusEncodingSize,
        int elidedNetworkEncodingSize,
        TxType type,
        ulong? expiryDeadline,
        UInt256[]? nonceKeys,
        Address? paymaster,
        UInt256 maxFee)
    {
        Type = type;
        Hash = hash;
        SenderAddress = sender;
        Nonce = nonce;
        Value = value;
        GasLimit = gasLimit;
        GasPrice = gasPrice; // means MaxPriorityFeePerGas
        DecodedMaxFeePerGas = maxFeePerGas;
        // The record writes max_fee_per_blob_gas as zero for a frame transaction, which has none.
        MaxFeePerBlobGas = type.SupportsFrames() ? null : maxFeePerBlobGas;
        MaxFee = maxFee;
        BlobVersionedHashes = blobVersionHashes;
        Timestamp = timestamp;
        ProofVersion = proofVersion;
        BlobCellMask = blobCellMask;
        _consensusEncodingSize = consensusEncodingSize;
        _elidedNetworkEncodingSize = elidedNetworkEncodingSize;
        PersistedExpiryDeadline = expiryDeadline;
        NonceKeys = nonceKeys;
        // The slot this record already holds against its sponsor, so the cap keeps counting it after a reload.
        PersistedPaymaster = paymaster;
        NetworkLength = size;
        if (consensusEncodingSize > 0) _size = consensusEncodingSize;
    }

    public ProofVersion? ProofVersion { get; set; }

    /// <summary>Size in bytes of the full transaction's mempool (network) encoding, sidecar included.</summary>
    public int NetworkLength { get; private set; }

    /// <summary>The full transaction the blob pool stores when it inserts this record; the pool clears it on insert.</summary>
    internal MempoolTransaction? PendingInsert { get; set; }

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

    internal void UpdateBlobPoolMetadata(MempoolTransaction blobTx)
    {
        NetworkLength = blobTx.GetLength();
        if (Volatile.Read(ref _elidedNetworkEncodingSize) == 0)
        {
            Volatile.Write(ref _elidedNetworkEncodingSize, blobTx.GetElidedNetworkEncodingSize());
        }

        BlobCellMask = blobTx.NetworkWrapper?.GetAvailableCellMask() ?? default;
    }

    public int GetConsensusEncodingSize() => _consensusEncodingSize;

    /// <summary>
    /// Size in bytes of the blob-elided eth/72 network encoding of this transaction, or <c>0</c> when it cannot
    /// be derived from the persisted metadata. See <see cref="TransactionExtensions.GetElidedNetworkEncodingSize"/>.
    /// </summary>
    public int GetElidedNetworkEncodingSize()
    {
        int elidedNetworkEncodingSize = Volatile.Read(ref _elidedNetworkEncodingSize);
        return elidedNetworkEncodingSize > 0
            ? elidedNetworkEncodingSize
            : TransactionExtensions.CalculateElidedNetworkEncodingSize(
                _consensusEncodingSize,
                ProofVersion,
                BlobVersionedHashes?.Length ?? 0);
    }
}
