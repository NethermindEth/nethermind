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
        Type = TxType.Blob;
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
        BlobCellMask = (fullTx.NetworkWrapper as ShardBlobNetworkWrapper)?.GetAvailableCellMask() ?? default;
        _consensusEncodingSize = fullTx.GetLength(shouldCountBlobs: false);
        _size = fullTx.GetLength();
    }

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
        : this(
            timestamp,
            sender,
            nonce,
            hash,
            value,
            gasLimit,
            gasPrice,
            maxFeePerGas,
            maxFeePerBlobGas,
            blobVersionHashes,
            poolIndex,
            size,
            proofVersion,
            default,
            0)
    {
    }

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
        BlobCellMask blobCellMask = default,
        int sparseBlobNetworkSize = 0)
    {
        Type = TxType.Blob;
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
        _size = size;
    }

    public ProofVersion? ProofVersion { get; set; }

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
        set => Volatile.Write(ref _blobCellMask, new StrongBox<BlobCellMask>(value));
    }

    public override ProofVersion? GetProofVersion() => ProofVersion;

    public int GetConsensusEncodingSize() => _consensusEncodingSize;

    public int GetSparseBlobNetworkSize() => _consensusEncodingSize;
}
