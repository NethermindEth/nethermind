// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Core;

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
        MaxFeePerDataGas = fullTx.MaxFeePerDataGas;
        BlobVersionedHashes = new byte[fullTx.BlobVersionedHashes!.Length][];
        GasBottleneck = fullTx.GasBottleneck;
    }
}
