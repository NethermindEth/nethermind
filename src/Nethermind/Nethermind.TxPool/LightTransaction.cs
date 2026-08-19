// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// For sorting reasons - without storing full, large txs in memory
/// </summary>
public class LightTransaction : Transaction
{
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
        PayerExposure = fullTx.PayerExposure;
        PersistedExpiryDeadline = FrameTxValidation.TryGetExpiryDeadline(fullTx, out ulong deadline) ? deadline : null;
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
            blobVersionHashes, poolIndex, size, proofVersion, TxType.Blob)
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
        TxType type,
        ulong? expiryDeadline = null,
        Address? payerAddress = null,
        UInt256? payerExposure = null)
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
        PersistedExpiryDeadline = expiryDeadline;
        // The reservation this record already holds in the pool's ledger, so its removal releases what
        // admission took rather than nothing.
        PayerAddress = payerAddress;
        PayerExposure = payerExposure;
        _size = size;
    }

    public ProofVersion? ProofVersion { get; set; }

    /// <inheritdoc/>
    public override ulong? PersistedExpiryDeadline { get; }

    public override ProofVersion? GetProofVersion() => ProofVersion;
}
