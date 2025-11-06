// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulatePayload<T>
{
    /// <summary>
    ///     Definition of blocks that can contain calls and overrides
    /// </summary>
    public List<BlockStateCall<T>>? BlockStateCalls { get; set; }

    /// <summary>
    ///     Should trace ETH Transfers
    /// </summary>
    public bool TraceTransfers { get; set; } = false;

    /// <summary>
    ///     When true, the simulate does all validations that a normal EVM would do, except contract sender and signature
    ///     checks. When false, multicall behaves like eth_call.
    /// </summary>
    public bool Validation { get; set; } = false;

    public bool ReturnFullTransactions
    {
        set => ReturnFullTransactionObjects = value;
        get => ReturnFullTransactionObjects;
    }

    /// <summary>
    /// When true, the simulate returns Full Tx Objects
    /// </summary>
    public bool ReturnFullTransactionObjects { get; set; } = true;
}
