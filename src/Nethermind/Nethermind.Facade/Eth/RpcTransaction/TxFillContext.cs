// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Node-computed defaults handed to <see cref="TransactionForRpc.FillDefaults"/> so each transaction
/// type populates the fields it uses without the RPC layer branching on the concrete type.
/// </summary>
public readonly struct TxFillContext
{
    public required UInt256 GasPrice { get; init; }

    public required UInt256 MaxPriorityFeePerGas { get; init; }

    public required UInt256 BaseFee { get; init; }

    /// <summary>Current blob base fee, or <c>null</c> when it cannot be computed.</summary>
    public required UInt256? BlobBaseFee { get; init; }

    public required IReleaseSpec Spec { get; init; }
}
