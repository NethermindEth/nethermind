// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        Type = TxType.Blob;
        Hash = fullTx.Hash;
        SenderAddress = fullTx.SenderAddress;
        Nonce = fullTx.Nonce;
        Value = fullTx.Value;
        GasLimit = fullTx.GasLimit;
        GasPrice = fullTx.GasPrice; // means MaxPriorityFeePerGas
        DecodedMaxFeePerGas = fullTx.DecodedMaxFeePerGas;
        MaxFeePerBlobGas = fullTx.MaxFeePerBlobGas;
        BlobVersionedHashes = new byte[fullTx.BlobVersionedHashes!.Length][];
        GasBottleneck = fullTx.GasBottleneck;
        Timestamp = fullTx.Timestamp;
        PoolIndex = fullTx.PoolIndex;
        _size = fullTx.GetLength();
    }

    public LightTransaction(
        UInt256 timestamp,
        Address sender,
        UInt256 nonce,
        Keccak hash,
        UInt256 value,
        long gasLimit,
        UInt256 gasPrice,
        UInt256 maxFeePerGas,
        UInt256 maxFeePerBlobGas,
        byte[][] blobVersionHashes,
        ulong poolIndex,
        int size)
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
        _size = size;
    }
}
