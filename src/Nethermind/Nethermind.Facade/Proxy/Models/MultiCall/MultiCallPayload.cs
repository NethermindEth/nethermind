// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class MultiCallPayload
{
    //Definition of blocks that can contain calls and overrides
    public BlockStateCalls[]? BlockStateCalls { get; set; }

    //Trace ETH Transfers
    public bool TraceTransfers { get; set; } = false;

    //When true, the multicall does all validations that a normal EVM would do, except contract sender and signature checks. When false, multicall behaves like eth_call.
    public bool Validation { get; set; } = false;

}
