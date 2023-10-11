// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class MultiCallPayload<T>
{
    /// <summary>
    /// Definition of blocks that can contain calls and overrides
    /// </summary>
    public BlockStateCall<T>[]? BlockStateCalls { get; set; }

    /// <summary>
    /// Should trace ETH Transfers
    /// </summary>
    public bool TraceTransfers { get; set; }

    /// <summary>
    /// When true, the multicall does all validations that a normal EVM would do, except contract sender and signature checks. When false, multicall behaves like eth_call.
    /// </summary>
    public bool Validation { get; set; }
}
