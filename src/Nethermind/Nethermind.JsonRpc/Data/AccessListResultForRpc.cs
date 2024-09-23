// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public readonly struct AccessListResultForRpc
{
    public AccessListForRpc AccessList { get; init; }

    public UInt256 GasUsed { get; init; }

    public AccessListResultForRpc(AccessListForRpc accessList, in UInt256 gasUsed)
    {
        AccessList = accessList;
        GasUsed = gasUsed;
    }
}
