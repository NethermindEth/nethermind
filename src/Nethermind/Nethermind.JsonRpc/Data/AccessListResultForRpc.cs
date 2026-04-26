// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public readonly struct AccessListResultForRpc(AccessListForRpc accessList, in UInt256 gasUsed, string? error)
{
    public AccessListForRpc AccessList { get; init; } = accessList;

    public UInt256 GasUsed { get; init; } = gasUsed;

    public string? Error { get; init; } = error;
}
