// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Node-computed defaults handed to <see cref="TransactionForRpc.PrepareForGasEstimation"/> and
/// <see cref="TransactionForRpc.FillFeeDefaults"/> so each transaction type populates the fields it
/// uses without the RPC layer branching on the concrete type.
/// </summary>
public readonly struct TxFillContext
{
    /// <summary>Suggested legacy gas price, used by legacy and access-list transactions.</summary>
    public required UInt256 GasPrice { get; init; }

    /// <summary>Suggested priority fee, used by EIP-1559 and later transactions.</summary>
    public required UInt256 MaxPriorityFeePerGas { get; init; }

    /// <summary>Current base fee, used to derive the default max fee per gas.</summary>
    public required UInt256 BaseFee { get; init; }

    /// <summary>Current blob base fee, or <c>null</c> when it cannot be computed.</summary>
    public required UInt256? BlobBaseFee { get; init; }

    /// <summary>Spec at the head block, used for the blob proof version and blob-count limits.</summary>
    public required IReleaseSpec Spec { get; init; }
}
