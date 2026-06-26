// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
    private readonly int _sparseBlobNetworkSize;

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
        _sparseBlobNetworkSize = fullTx.TryCalculateSparseBlobNetworkSize() ?? 0;
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
        _sparseBlobNetworkSize = sparseBlobNetworkSize;
        _size = size;
    }

    public ProofVersion? ProofVersion { get; set; }
    public BlobCellMask BlobCellMask { get; set; }

    public override ProofVersion? GetProofVersion() => ProofVersion;

    public int GetSparseBlobNetworkSize() => _sparseBlobNetworkSize;
}
