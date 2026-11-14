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

    /// <summary>The release spec of the including block, or of the next block for a pending transaction, which prices the gas a frame transaction reports.</summary>
    public IReleaseSpec? Spec { get; }

    /// <summary>The including block's excess blob gas, which prices the blob fee in a frame transaction's gas price.</summary>
    public ulong? ExcessBlobGas { get; }

    public TransactionForRpcContext(ulong chainId) : this(chainId, null) { }

    private TransactionForRpcContext(ulong chainId, IReleaseSpec? spec)
    {
        ChainId = chainId;
        BlockHash = null;
        BlockNumber = null;
        BlockTimestamp = null;
        TxIndex = null;
        BaseFee = null;
        Receipt = null;
        Spec = spec;
        ExcessBlobGas = null;
    }

    /// <summary>A context for a transaction not yet included, priced under the spec of the block after <paramref name="head"/>.</summary>
    /// <remarks>A frame transaction then reports the same <c>gas</c> before and after it is included in that block.</remarks>
    public static TransactionForRpcContext ForPending(ISpecProvider specProvider, BlockHeader? head, ulong secondsPerSlot) =>
        new(specProvider.ChainId, head is null ? null : specProvider.GetSpec(head.Number + 1, head.Timestamp + secondsPerSlot));

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
