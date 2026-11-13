// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth;

public interface IFromTransaction<out T> : ITxTyped where T : TransactionForRpc
{
    static abstract T FromTransaction(Transaction tx, in TransactionForRpcContext extraData);
}

public readonly record struct TransactionForRpcContext
{
    public ulong? ChainId { get; }
    public Hash256? BlockHash { get; }
    public ulong? BlockNumber { get; }
    public ulong? BlockTimestamp { get; }
    public int? TxIndex { get; }
    public UInt256? BaseFee { get; }
    public TxReceipt? Receipt { get; }

    /// <summary>The release spec of the including block, which prices the gas a frame transaction reports.</summary>
    public IReleaseSpec? Spec { get; }

    /// <summary>The including block's excess blob gas, which prices the blob fee in a frame transaction's gas price.</summary>
    public ulong? ExcessBlobGas { get; }

    public TransactionForRpcContext(ulong chainId)
    {
        ChainId = chainId;
        BlockHash = null;
        BlockNumber = null;
        BlockTimestamp = null;
        TxIndex = null;
        BaseFee = null;
        Receipt = null;
        Spec = null;
        ExcessBlobGas = null;
    }

    public TransactionForRpcContext(
        ulong chainId,
        Hash256 blockHash,
        ulong blockNumber,
        int txIndex,
        ulong blockTimestamp,
        UInt256 baseFee,
        IReleaseSpec spec,
        TxReceipt? receipt = null,
        ulong? excessBlobGas = null)
    {
        ChainId = chainId;
        BlockHash = blockHash;
        BlockNumber = blockNumber;
        BlockTimestamp = blockTimestamp;
        TxIndex = txIndex;
        BaseFee = baseFee;
        Receipt = receipt;
        Spec = spec;
        ExcessBlobGas = excessBlobGas;
    }
}
